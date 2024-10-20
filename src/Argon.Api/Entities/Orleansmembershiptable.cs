using System;
using System.Collections.Generic;

namespace Argon.Api;

public partial class Orleansmembershiptable
{
    public string Deploymentid { get; set; } = null!;

    public string Address { get; set; } = null!;

    public int Port { get; set; }

    public int Generation { get; set; }

    public string Siloname { get; set; } = null!;

    public string Hostname { get; set; } = null!;

    public int Status { get; set; }

    public int? Proxyport { get; set; }

    public string? Suspecttimes { get; set; }

    public DateTime Starttime { get; set; }

    public DateTime Iamalivetime { get; set; }

    public virtual Orleansmembershipversiontable Deployment { get; set; } = null!;
}
