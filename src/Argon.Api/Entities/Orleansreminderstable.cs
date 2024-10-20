using System;
using System.Collections.Generic;

namespace Argon.Api;

public partial class Orleansreminderstable
{
    public string Serviceid { get; set; } = null!;

    public string Grainid { get; set; } = null!;

    public string Remindername { get; set; } = null!;

    public DateTime Starttime { get; set; }

    public long Period { get; set; }

    public int Grainhash { get; set; }

    public int Version { get; set; }
}
