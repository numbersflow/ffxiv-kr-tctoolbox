using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TCToolbox.Core;
using TCToolbox.Modules;

namespace TCToolbox;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;
    public HashSet<string> EnabledModules { get; set; } = [];
    public AutoGardensWorkConfig GardensWork { get; set; } = new() { AutoLoopEnabled = false };
    public FleetEmergencyStopConfig FleetEmergencyStop { get; set; } = new();
    // Preserve settings for upstream modules omitted from this gardening build.
    [JsonExtensionData] public IDictionary<string, JToken>? OtherSettings { get; set; }
    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}

public sealed class Plugin : IDalamudPlugin
{
    public static Plugin Instance { get; private set; } = null!;
    public Configuration Config { get; }
    public List<TcModule> Modules { get; } = [];
    private readonly AutoGardensWork garden = new();
    private readonly GardeningIpc ipc;
    private readonly GardeningCommand command;
    private bool open;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();
        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new();
        AddonPressGuard.EnsureWatching();
        Modules.Add(garden);
        ipc = new GardeningIpc();
        command = new GardeningCommand();
        Svc.Commands.AddHandler("/tctoolbox", new CommandInfo((_, _) => OpenMainWindow()) { HelpMessage = "한국 서버 재배 설정" });
        Svc.PluginInterface.UiBuilder.Draw += Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        if (Config.EnabledModules.Contains(garden.InternalName)) garden.Enable();
        Svc.Log.Information("TC Gardening KR API 15 loaded; only the upstream gardening module is included.");
    }

    public void OpenMainWindow() => open = true;

    private void Draw()
    {
        if (!open) return;
        try
        {
            if (!ImGui.Begin("TC 재배 KR###TCToolboxGardeningKR", ref open)) return;
            var enabled = garden.IsEnabled;
            if (ImGui.Checkbox("재배 모듈 사용", ref enabled))
            {
                if (enabled) garden.Enable(); else garden.Disable();
                if (garden.IsEnabled) Config.EnabledModules.Add(garden.InternalName);
                else Config.EnabledModules.Remove(garden.InternalName);
                Config.Save();
            }
            ImGui.TextWrapped("화단·화분 설정 후 실행하세요. 워크플로우 목표·일정 연결은 아직 포함되지 않았습니다.");
            garden.DrawConfig();
        }
        finally { ImGui.End(); }
    }

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        Svc.Commands.RemoveHandler("/tctoolbox");
        command.Dispose();
        ipc.Dispose();
        garden.Disable();
        NavStop.ForceTeardown();
        TextAdvanceLock.ForceTeardown();
        ModuleFailureNotice.ForceTeardown();
        AddonPressGuard.ForceTeardown();
    }
}
