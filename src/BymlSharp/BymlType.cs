namespace BymlSharp;

public enum BymlType : byte
{
    None = 0x00,

    HashMap32 = 0x20,

    HashMap64 = 0x21,

    String = 0xA0,
    Binary = 0xA1,
    BinaryAligned = 0xA2,

    Array = 0xC0,
    Map = 0xC1,
    StringTable = 0xC2,

    Bool = 0xD0,
    Int = 0xD1,
    Float = 0xD2,
    UInt = 0xD3,
    Int64 = 0xD4,
    UInt64 = 0xD5,
    Double = 0xD6,

    Null = 0xFF,
}
