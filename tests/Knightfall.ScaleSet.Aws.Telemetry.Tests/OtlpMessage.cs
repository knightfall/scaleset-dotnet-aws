using System.Text;
using Google.Protobuf;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

internal sealed class OtlpMessage
{
    private readonly Dictionary<int, List<object>> _fields = [];

    internal OtlpMessage(byte[] payload)
    {
        using var parser = new CodedInputStream(payload);
        uint tag;
        while ((tag = parser.ReadTag()) != 0)
        {
            var number = (int)(tag >> 3);
            if (!_fields.TryGetValue(number, out var values))
            {
                values = [];
                _fields.Add(number, values);
            }
            values.Add((tag & 7) switch
            {
                0 => parser.ReadUInt64(),
                1 => parser.ReadFixed64(),
                2 => parser.ReadBytes().ToByteArray(),
                5 => parser.ReadFixed32(),
                _ => throw new InvalidDataException("Unexpected protobuf wire type."),
            });
        }
    }

    internal OtlpMessage Message(int number) => new(Bytes(number));

    internal IEnumerable<OtlpMessage> Messages(int number) =>
        _fields.TryGetValue(number, out var values) ? values.Select(static value => new OtlpMessage((byte[])value)) : [];

    internal string Text(int number) => Encoding.UTF8.GetString(Bytes(number));

    internal byte[] Bytes(int number) => (byte[])_fields[number].Single();

    internal byte[] BytesOrEmpty(int number) => _fields.TryGetValue(number, out var values) ? (byte[])values.Single() : [];

    internal ulong Number(int number) => (ulong)_fields[number].Single();

    internal Dictionary<string, string> StringAttributes(int number) => Messages(number).ToDictionary(
        static attribute => attribute.Text(1),
        static attribute => attribute.Message(2).Text(1),
        StringComparer.Ordinal);

    internal IEnumerable<string> AttributeNames(int number) => Messages(number).Select(static attribute => attribute.Text(1));
}
