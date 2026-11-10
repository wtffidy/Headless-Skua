using Microsoft.Extensions.DependencyInjection;
using Skua.Core.AppStartup;
using Skua.Core.GameProxy;
using Skua.Core.Interfaces;
using Skua.Core.Options;
using Skua.Core.Plugins;
using Skua.Core.Scripts;
using Skua.Core.Scripts.Helpers;
using Skua.Core.Services;
using Skua.Core.Skills;

namespace HeadlessSkua;

/// <summary>
/// Registers the existing Skua scripting engine without registering desktop UI services.
/// </summary>
internal static class HeadlessServices
{
    /// <summary>
    /// Adds the existing Skua core, script helpers, and headless service implementations.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="flash">The active Ruffle-backed Flash compatibility adapter.</param>
    /// <returns>The configured service collection.</returns>
    public static IServiceCollection AddHeadlessSkua(this IServiceCollection services, IFlashUtil flash)
    {
        services.AddCommonServices();

        services.AddSingleton<IFlashUtil>(flash);
        services.AddSingleton<ISettingsService, HeadlessSettingsService>();
        services.AddSingleton<IDialogService, HeadlessDialogService>();
        services.AddSingleton<ILogService, HeadlessLogService>();
        services.AddTransient(Skua.Core.AppStartup.Services.CreateCompiler);

        services.AddSingleton<ScriptManager>();
        services.AddSingleton<IScriptManager>(provider => provider.GetRequiredService<ScriptManager>());
        services.AddSingleton<IScriptStatus>(provider => provider.GetRequiredService<ScriptManager>());
        services.AddSingleton<IScriptInterface, ScriptInterface>();

        services.AddSingleton<IScriptInventoryHelper, ScriptInventoryHelper>();
        services.AddSingleton<IScriptInventory, ScriptInventory>();
        services.AddSingleton<IScriptHouseInv, ScriptHouseInv>();
        services.AddSingleton<IScriptTempInv, ScriptTempInv>();
        services.AddSingleton<IScriptBank, ScriptBank>();

        services.AddSingleton<IAdvancedSkillContainer, AdvancedSkillContainer>();
        services.AddSingleton<IUltraBossHelper, UltraBossHelper>();
        services.AddSingleton<IScriptCombat, ScriptCombat>();
        services.AddSingleton<IScriptKill, ScriptKill>();
        services.AddSingleton<IScriptHunt, ScriptHunt>();
        services.AddSingleton<IScriptSkill, ScriptSkill>();
        services.AddSingleton<IScriptAuto, ScriptAuto>();
        services.AddSingleton<IScriptSelfAuras, ScriptSelfAuras>();
        services.AddSingleton<IScriptTargetAuras, ScriptTargetAuras>();

        services.AddSingleton<IScriptFaction, ScriptFaction>();
        services.AddSingleton<IScriptMonster, ScriptMonster>();
        services.AddSingleton<IScriptPlayer, ScriptPlayer>();
        services.AddSingleton<IScriptQuest, ScriptQuest>();
        services.AddSingleton<IScriptBoost, ScriptBoost>();
        services.AddSingleton<IScriptShop, ScriptShop>();
        services.AddSingleton<IScriptDrop, ScriptDrop>();
        services.AddSingleton<IScriptMap, ScriptMap>();

        services.AddSingleton<IScriptServers, ScriptServers>();
        services.AddSingleton<IScriptEvent, ScriptEvent>();
        services.AddSingleton<IScriptSend, ScriptSend>();

        services.AddTransient<IScriptOptionContainer, ScriptOptionContainer>();
        services.AddTransient<IOptionContainer, OptionContainer>();
        services.AddSingleton<IScriptOption, ScriptOption>();
        services.AddSingleton<IScriptLite, ScriptLite>();

        services.AddSingleton<IScriptBotStats, ScriptBotStats>();
        services.AddSingleton<IScriptHandlers, ScriptHandlers>();
        services.AddSingleton<IScriptWait, ScriptWait>();
        services.AddSingleton<IScriptAccounts, ScriptAccounts>();

        services.AddSingleton<ICaptureProxy, CaptureProxy>();

        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddTransient<IPluginContainer, PluginContainer>();
        services.AddSingleton<IPluginHelper, PluginHelper>();

        services.AddSingleton<IMapService, MapService>();
        services.AddSingleton<IQuestDataLoaderService, QuestDataLoaderService>();
        services.AddSingleton<IGrabberService, GrabberService>();
        services.AddSingleton<IClientFilesService, ClientFilesService>();
        services.AddSingleton<IAuraMonitorService, AuraMonitorService>();
        services.AddSingleton<IJunkService, JunkService>();
        services.AddSingleton<BackgroundThemeService>();

        return services;
    }
}
