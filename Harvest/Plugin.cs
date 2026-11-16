using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Harvest.Core;
using Harvest.Modules;
using Harvest.Planning;

namespace Harvest;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;
    public HashSet<string> EnabledModules { get; set; } = [];
    public AutoGardensWorkConfig GardensWork { get; set; } = new() { AutoLoopEnabled = false };
    public FleetEmergencyStopConfig FleetEmergencyStop { get; set; } = new();
    public Dictionary<ulong, HarvestProfile> Profiles { get; set; } = [];
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
    private readonly HarvestService service;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<object> openGate;
    private bool open;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();
        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? LoadLegacyConfig();
        AddonPressGuard.EnsureWatching();
        Modules.Add(garden);
        service = new HarvestService(Config, garden);
        openGate = pluginInterface.GetIpcProvider<object>("Harvest.OpenSettings");
        openGate.RegisterAction(OpenMainWindow);
        Svc.Commands.AddHandler("/harvest", new CommandInfo((_, _) => OpenMainWindow()) { HelpMessage = "하베스트 재배 목표·시설 설정" });
        Svc.PluginInterface.UiBuilder.Draw += Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        Config.GardensWork.AutoLoopEnabled = false;
        Config.Save();
        Svc.Log.Information("Harvest API 15 loaded; gardening-only workflow worker.");
    }

    public void OpenMainWindow()
    {
        var workflow = Svc.PluginInterface.GetIpcSubscriber<object>("WorkflowOrchestrator.OpenGardens");
        if (workflow.HasAction) { workflow.InvokeAction(); return; }
        open = true;
    }

    private static Configuration LoadLegacyConfig()
    {
        var legacy = Path.Combine(Svc.PluginInterface.ConfigFile.DirectoryName!, "TCToolbox.json");
        if (!File.Exists(legacy)) return new();
        try {
            var backup = legacy + ".before-harvest-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(legacy, backup, false);
            var previous = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(legacy)) ?? new();
            // Legacy module switches never authorize starting the new workflow worker.
            previous.EnabledModules.Clear(); previous.GardensWork.AutoLoopEnabled = false;
            return previous;
        } catch (Exception ex) { Svc.Log.Error(ex, "Legacy garden configuration could not be imported"); return new(); }
    }

    private void Draw()
    {
        if (!open) return;
        try
        {
            if (!ImGui.Begin("하베스트###Harvest", ref open)) return;
            service.Draw();
        }
        finally { ImGui.End(); }
    }

    public void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        Svc.Commands.RemoveHandler("/harvest");
        openGate.UnregisterAction();
        service.Dispose();
        garden.Disable();
        NavStop.ForceTeardown();
        TextAdvanceLock.ForceTeardown();
        ModuleFailureNotice.ForceTeardown();
        AddonPressGuard.ForceTeardown();
    }
}
