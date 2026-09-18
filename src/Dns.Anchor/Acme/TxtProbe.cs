using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.KGSM.Dns.Anchor.Acme;

/// <summary>
/// Asks one nameserver, directly, which TXT values a name carries.
/// </summary>
/// <remarks>
/// <para>
/// Asked of the zone's own servers rather than a resolver, because a resolver answers from its cache: a
/// name looked up before its challenge record existed stays "nothing here" at a resolver until the
/// negative answer expires, while the zone's own servers answer with what the zone holds now. The
/// issuer validates against the zone, so the zone is the thing to ask.
/// </para>
/// <para>
/// One UDP question and one answer, parsed only as far as TXT records need. .NET resolves addresses
/// and nothing else, so the question is built here.
/// </para>
/// </remarks>
public static class TxtProbe
{
    /// <summary>The TXT strings at <paramref name="name"/>, as <paramref name="server"/> answers.</summary>
    /// <returns>Each record's strings joined, one entry per record. Empty when the server holds none.</returns>
    public static async Task<IReadOnlyList<string>> QueryAsync(
        IPAddress server, string name, TimeSpan timeout, CancellationToken ct)
    {
        byte[] question = BuildQuery(name, out ushort id);

        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server, 53);
        await udp.SendAsync(question, ct).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        while (true)
        {
            UdpReceiveResult received = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
            byte[] answer = received.Buffer;

            // A datagram that is not the answer to this question is ignored rather than trusted.
            if (answer.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(answer) == id)
                return ParseTxt(answer);
        }
    }

    internal static byte[] BuildQuery(string name, out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);

        var packet = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        // Flags zero: a standard query that asks for no recursion, because it is put to the server that
        // holds the zone and the answer wanted is that server's own.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // one question
        packet.AddRange(header.ToArray());

        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }

        packet.Add(0);
        packet.AddRange([0, 16, 0, 1]); // type TXT, class IN
        return [.. packet];
    }

    internal static IReadOnlyList<string> ParseTxt(byte[] message)
    {
        var values = new List<string>();

        int questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        int offset = 12;

        for (int i = 0; i < questions; i++)
            offset = SkipName(message, offset) + 4;

        for (int i = 0; i < answers && offset < message.Length; i++)
        {
            offset = SkipName(message, offset);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            int data = offset + 10;

            if (type == 16)
            {
                var text = new StringBuilder();
                int cursor = data;
                while (cursor < data + length)
                {
                    int chunk = message[cursor];
                    text.Append(Encoding.ASCII.GetString(message, cursor + 1, chunk));
                    cursor += 1 + chunk;
                }

                values.Add(text.ToString());
            }

            offset = data + length;
        }

        return values;
    }

    /// <summary>Step past a name, following the format's compression rule: a pointer ends it.</summary>
    private static int SkipName(byte[] message, int offset)
    {
        while (offset < message.Length)
        {
            byte length = message[offset];
            if (length == 0)
                return offset + 1;

            if ((length & 0xC0) == 0xC0)
                return offset + 2;

            offset += 1 + length;
        }

        return offset;
    }
}
