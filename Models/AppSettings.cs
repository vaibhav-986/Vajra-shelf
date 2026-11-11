using System;

namespace VajraShelf.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "#8FE3CF";
    public double Scale { get; set; } = 1;
    public double TopOffset { get; set; } = 82;
    public int MonitorIndex { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public bool HideInFullscreen { get; set; }
    public bool StartAtLogin { get; set; }
    public int TemporaryLifetimeMinutes { get; set; } = 1440;
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
