using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PcToolkit.Core.Logging;
using PcToolkit.Core.Service;

namespace PcToolkit.Service;

/// <summary>
/// Локальный именованный канал службы.
/// Подключаться могут вошедшие в систему пользователи, создавать свои экземпляры канала — только SYSTEM и администраторы,
/// поэтому подменить службу обычная программа не может. Отвечаем только процессу PcToolkit.exe;
/// а что именно можно изменить, решает <see cref="PrivilegedChangeValidator"/>.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private const int MaxClients = 8;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Func<ServiceRequest, ServiceResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PipeServer(Func<ServiceRequest, ServiceResponse> handler)
    {
        _handler = handler;
    }

    public void Start() => _loop = Task.Run(() => AcceptLoop(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoop(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (IOException)
            {
                // Все экземпляры заняты — ждём, пока кто-нибудь отключится.
                await Delay(500, cancellation);
                continue;
            }
            catch (Exception ex)
            {
                AppLog.Error("Не удалось создать канал службы.", ex);
                await Delay(2000, cancellation);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ошибка ожидания подключения к службе.", ex);
                await pipe.DisposeAsync();
                continue;
            }

            _ = Task.Run(() => Serve(pipe, cancellation), cancellation);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ServiceProtocol.PipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        await using (pipe)
        {
            string? client = DescribeClient(pipe, out bool trusted);
            if (!trusted)
            {
                AppLog.Warn($"Отклонено подключение к службе: {client ?? "неизвестный процесс"}.");
                return;
            }

            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true);
            try
            {
                while (!cancellation.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line = await reader.ReadLineAsync(cancellation);
                    if (line is null)
                        break;

                    ServiceResponse response;
                    try
                    {
                        ServiceRequest? request = JsonSerializer.Deserialize<ServiceRequest>(line, ServiceProtocol.Json);
                        response = request is null ? ServiceResponse.Fail("Пустой запрос.") : _handler(request);
                    }
                    catch (JsonException)
                    {
                        response = ServiceResponse.Fail("Некорректный запрос.");
                    }

                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, ServiceProtocol.Json));
                    await writer.FlushAsync(cancellation);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Приложение закрылось — обычное дело.
            }
        }
    }

    /// <summary>Описание подключившегося процесса; доверяем только PcToolkit.exe в сеансе пользователя.</summary>
    private static string? DescribeClient(NamedPipeServerStream pipe, out bool trusted)
    {
        trusted = false;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
                return null;

            using Process process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            trusted = process.SessionId != 0
                      && string.Equals(Path.GetFileName(path), ServiceProtocol.ClientExecutable, StringComparison.OrdinalIgnoreCase);
            return $"{path} (PID {processId})";
        }
        catch (Exception ex)
        {
            AppLog.Warn("Не удалось проверить процесс, подключившийся к службе.", ex);
            return null;
        }
    }

    private static async Task Delay(int milliseconds, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(milliseconds, cancellation);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
