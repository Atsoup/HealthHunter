using System.Buffers.Binary;

namespace HealthHunter;

public enum ValueKind { Int32, Float, Double, Int16, Int64, Byte, UInt16, UInt32 }

public static class ValueKindExt
{
    public static int Size(this ValueKind k) => k switch
    {
        ValueKind.Byte => 1,
        ValueKind.Int16 or ValueKind.UInt16 => 2,
        ValueKind.Int32 or ValueKind.UInt32 or ValueKind.Float => 4,
        _ => 8
    };

    /// <summary>Scan stride. Games keep numbers naturally aligned, so this is fast and rarely misses.</summary>
    public static int Alignment(this ValueKind k) => Math.Min(k.Size(), 4);

    public static bool IsFloating(this ValueKind k) => k is ValueKind.Float or ValueKind.Double;

    /// <summary>The types health is almost always stored as. "Auto" scans all three at once.</summary>
    public static readonly ValueKind[] HealthKinds = { ValueKind.Int32, ValueKind.Float, ValueKind.Double };

    public static string KindsLabel(IReadOnlyList<ValueKind> kinds) =>
        kinds.Count == 1 ? kinds[0].ToString() : string.Join(" + ", kinds);

    public static double Decode(this ValueKind k, ReadOnlySpan<byte> b) => k switch
    {
        ValueKind.Byte => b[0],
        ValueKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b),
        ValueKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(b),
        ValueKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(b),
        ValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b),
        ValueKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b),
        ValueKind.Float => BinaryPrimitives.ReadSingleLittleEndian(b),
        ValueKind.Double => BinaryPrimitives.ReadDoubleLittleEndian(b),
        _ => 0
    };

    public static void Encode(this ValueKind k, double v, Span<byte> b)
    {
        switch (k)
        {
            case ValueKind.Byte: b[0] = (byte)Math.Clamp(Math.Round(v), 0, 255); break;
            case ValueKind.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Math.Clamp(Math.Round(v), 0, ushort.MaxValue)); break;
            case ValueKind.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Clamp(Math.Round(v), 0, uint.MaxValue)); break;
            case ValueKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(b, (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)); break;
            case ValueKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue)); break;
            case ValueKind.Int64: BinaryPrimitives.WriteInt64LittleEndian(b, (long)Math.Round(v)); break;
            case ValueKind.Float: BinaryPrimitives.WriteSingleLittleEndian(b, (float)v); break;
            case ValueKind.Double: BinaryPrimitives.WriteDoubleLittleEndian(b, v); break;
        }
    }

    public static string Format(this ValueKind k, double v) =>
        k.IsFloating() ? v.ToString("0.###") : ((long)v).ToString();
}
