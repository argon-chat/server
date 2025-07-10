namespace Argon.Cassandra.Mapping;

using global::Cassandra.Serialization;

public class ULongAsBigIntSerializer : TypeSerializer<ulong>
{
    public override ColumnTypeCode CqlType => ColumnTypeCode.Bigint;

    public override ulong Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
    {
        var signed = PrimitiveLongSerializer.Deserialize(protocolVersion, buffer, offset, length, typeInfo);
        return BitConverter.ToUInt64(BitConverter.GetBytes(signed));
    }

    public override byte[] Serialize(ushort protocolVersion, ulong value)
    {
        var signed = BitConverter.ToInt64(BitConverter.GetBytes(value));
        return PrimitiveLongSerializer.Serialize(protocolVersion, signed);
    }
}