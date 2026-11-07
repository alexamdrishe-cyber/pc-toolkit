using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PcToolkit.Core.Benchmarks;

/// <summary>Вид нагрузки на процессор — как галочки в стресс-тесте AIDA64.</summary>
public enum CpuWorkload
{
    /// <summary>Целочисленные вычисления (ALU).</summary>
    Integer,

    /// <summary>Вычисления с плавающей точкой: AVX2 + FMA, если процессор умеет. Самая «горячая» нагрузка.</summary>
    Float,

    /// <summary>Кэш процессора: случайные чтения и записи в блоке, который помещается в L2.</summary>
    Cache,

    /// <summary>Оперативная память: запись шаблона в большой блок и проверка при чтении.</summary>
    Memory,
}

/// <summary>
/// Вычислительные ядра тестов. Каждое детерминировано: при одинаковых параметрах результат всегда один и тот же.
/// Если на нагретом или разогнанном процессоре результат разошёлся с эталоном — это ошибка вычислений,
/// то есть признак нестабильности (так работают Prime95, OCCT и подобные тесты).
/// </summary>
public static class CpuKernels
{
    /// <summary>Сколько 64-битных слов в блоке для теста кэша: 256 КБ — помещается в L2 любого современного процессора.</summary>
    public const int CacheWords = 256 * 1024 / sizeof(ulong);

    // Все ядра — с AggressiveOptimization: JIT сразу компилирует их полностью оптимизированными,
    // без промежуточного уровня (tier 0 / OSR) и без заготовки ReadyToRun под базовый набор инструкций.
    // Иначе первые секунды замера шли на медленном коде, и результат зависел от сборки и прогрева.

    /// <summary>Целочисленная нагрузка: четыре независимые цепочки xorshift с умножениями и сдвигами.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong Integer(long rounds, ulong seed = 0x9E3779B97F4A7C15UL)
    {
        ulong a = seed, b = seed ^ 0xD1B54A32D192ED03UL, c = seed * 3 + 1, d = ~seed;
        ulong acc = 0;
        for (long i = 0; i < rounds; i++)
        {
            a ^= a << 13; a ^= a >> 7; a ^= a << 17;
            b ^= b << 5; b ^= b >> 9; b ^= b << 29;
            c = c * 6364136223846793005UL + 1442695040888963407UL;
            d = BitOperations.RotateLeft(d ^ c, 23) + a;
            acc += (a * b) ^ (c >> 3) ^ d;
        }

        return acc ^ a ^ b ^ c ^ d;
    }

    /// <summary>Операций в одном раунде <see cref="Integer"/> (для пересчёта в «операции в секунду»).</summary>
    public const int IntegerOpsPerRound = 20;

    /// <summary>
    /// Нагрузка с плавающей точкой: 12 независимых векторных аккумуляторов, a = a × m + c.
    /// Значения сходятся к 1.0, поэтому не бывает переполнений и денормализованных чисел.
    /// </summary>
    public static double Float(long rounds)
    {
        if (Fma.IsSupported && Avx.IsSupported)
            return FloatFma(rounds);

        return FloatPortable(rounds);
    }

    /// <summary>Операций с плавающей точкой в одном раунде (12 векторов × 4 числа × умножение и сложение).</summary>
    public static int FloatOpsPerRound => Fma.IsSupported && Avx.IsSupported ? 12 * 4 * 2 : 12 * Vector<double>.Count * 2;

    public static string FloatInstructionSet => Fma.IsSupported && Avx.IsSupported ? "AVX2 + FMA" : $"SIMD ({Vector<double>.Count * 64} бит)";

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double FloatFma(long rounds)
    {
        Vector256<double> m = Vector256.Create(0.9999999);
        Vector256<double> c = Vector256.Create(1e-7);
        Vector256<double> a0 = Vector256.Create(1.01), a1 = Vector256.Create(1.02), a2 = Vector256.Create(1.03),
                          a3 = Vector256.Create(1.04), a4 = Vector256.Create(1.05), a5 = Vector256.Create(1.06),
                          a6 = Vector256.Create(0.99), a7 = Vector256.Create(0.98), a8 = Vector256.Create(0.97),
                          a9 = Vector256.Create(0.96), a10 = Vector256.Create(0.95), a11 = Vector256.Create(0.94);

        for (long i = 0; i < rounds; i++)
        {
            a0 = Fma.MultiplyAdd(a0, m, c); a1 = Fma.MultiplyAdd(a1, m, c); a2 = Fma.MultiplyAdd(a2, m, c);
            a3 = Fma.MultiplyAdd(a3, m, c); a4 = Fma.MultiplyAdd(a4, m, c); a5 = Fma.MultiplyAdd(a5, m, c);
            a6 = Fma.MultiplyAdd(a6, m, c); a7 = Fma.MultiplyAdd(a7, m, c); a8 = Fma.MultiplyAdd(a8, m, c);
            a9 = Fma.MultiplyAdd(a9, m, c); a10 = Fma.MultiplyAdd(a10, m, c); a11 = Fma.MultiplyAdd(a11, m, c);
        }

        Vector256<double> sum = ((a0 + a1) + (a2 + a3)) + ((a4 + a5) + (a6 + a7)) + ((a8 + a9) + (a10 + a11));
        return Vector256.Sum(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static double FloatPortable(long rounds)
    {
        var m = new Vector<double>(0.9999999);
        var c = new Vector<double>(1e-7);
        var acc = new Vector<double>[12];
        for (int k = 0; k < acc.Length; k++)
            acc[k] = new Vector<double>(0.94 + k * 0.01);

        for (long i = 0; i < rounds; i++)
        {
            for (int k = 0; k < acc.Length; k++)
                acc[k] = acc[k] * m + c;
        }

        double sum = 0;
        foreach (Vector<double> v in acc)
            sum += Vector.Sum(v);
        return sum;
    }

    /// <summary>
    /// Нагрузка на кэш: псевдослучайные чтения и записи в блоке размером с L2.
    /// Блок заполняется заново при каждом вызове, поэтому результат детерминирован.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong Cache(ulong[] buffer, long rounds)
    {
        int mask = buffer.Length - 1;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (ulong)i * 0x9E3779B97F4A7C15UL;

        ulong acc = 0;
        int index = 1;
        for (long i = 0; i < rounds; i++)
        {
            index = (int)((uint)(index * 1103515245 + 12345) & (uint)mask);
            ulong value = buffer[index] + buffer[(index + 64) & mask] + (ulong)i;
            buffer[(index + 8) & mask] = value ^ (value >> 11);
            acc ^= value;
        }

        return acc;
    }

    /// <summary>
    /// Проверка памяти: записать в блок шаблон, зависящий от адреса и прохода, и прочитать обратно.
    /// Возвращает число слов, которые прочитались не так, как записались (в норме 0).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long Memory(ulong[] buffer, ulong pass)
    {
        ulong key = (pass + 1) * 0xBF58476D1CE4E5B9UL;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = ((ulong)i * 0x94D049BB133111EBUL) ^ key;

        long errors = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != (((ulong)i * 0x94D049BB133111EBUL) ^ key))
                errors++;
        }

        return errors;
    }
}
