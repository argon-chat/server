using System;
using System.Collections.Generic;

namespace Argon.Api;

public partial class Orleansstorage
{
    public int Grainidhash { get; set; }

    public long Grainidn0 { get; set; }

    public long Grainidn1 { get; set; }

    public int Graintypehash { get; set; }

    public string Graintypestring { get; set; } = null!;

    public string? Grainidextensionstring { get; set; }

    public string Serviceid { get; set; } = null!;

    public byte[]? Payloadbinary { get; set; }

    public DateTime Modifiedon { get; set; }

    public int? Version { get; set; }
}
