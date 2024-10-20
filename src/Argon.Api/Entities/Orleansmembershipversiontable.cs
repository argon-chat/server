using System;
using System.Collections.Generic;

namespace Argon.Api;

public partial class Orleansmembershipversiontable
{
    public string Deploymentid { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public int Version { get; set; }

    public virtual ICollection<Orleansmembershiptable> Orleansmembershiptables { get; set; } = new List<Orleansmembershiptable>();
}
