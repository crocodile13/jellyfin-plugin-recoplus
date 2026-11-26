using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecoPlus.Engine;

namespace Jellyfin.Plugin.RecoPlus.Integration;

/// <summary>
/// Page « Reco+ » dans le menu de chaque utilisateur, par Plugin Pages (s'il est installé) : ses propres réglages, ceux
/// que l'administrateur lui ouvre. Déclarée par réflexion (aucune dépendance de compilation), comme Home Screen Sections
/// le fait pour la sienne : <c>PluginInterface.RegisterPage(JObject)</c>, Plugin Pages 3 ou plus. Plugin Pages charge la
/// page (<c>RecoPlus/UserPage</c>) avec la session de l'utilisateur et demande à <see cref="IsEnabled"/> s'il faut la
/// montrer. Forme inattendue : rien n'est déclaré, l'état le dit.
/// </summary>
public sealed class PluginPagesIntegration : BackgroundService
{
    /// <summary>Identifiant de la page chez Plugin Pages.</summary>
    public const string PageId = "Jellyfin.Plugin.RecoPlus";

    private readonly ILogger<PluginPagesIntegration> _log;

    public PluginPagesIntegration(ILogger<PluginPagesIntegration> log) => _log = log;

    /// <summary>État lisible (emballé : traduit à l'affichage).</summary>
    public static string State { get; private set; } = "non tenté";

    /// <summary>
    /// Appelé par Plugin Pages à chaque menu : la page n'apparaît que si Reco+ est actif et que l'administrateur ouvre au
    /// moins un réglage aux utilisateurs.
    /// </summary>
    public static bool IsEnabled(string id)
        => Plugin.Instance?.Configuration is { Enabled: true, UserPageEnabled: true } c && (c.UserCanSetRegion || c.UserCanSetExtended);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // comme pour Home Screen Sections : les autres plugins sont chargés et démarrés
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        Register();
    }

    private void Register()
    {
        var asm = AssemblyLoadContext.All.SelectMany(c => c.Assemblies).FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.PluginPages");
        if (asm is null)
        {
            State = "Plugin Pages absent";
            return;
        }

        try
        {
            var iface = asm.GetType("Jellyfin.Plugin.PluginPages.PluginInterface");
            var register = iface?.GetMethod("RegisterPage", BindingFlags.Public | BindingFlags.Static);
            var jobjType = register?.GetParameters().FirstOrDefault()?.ParameterType;
            var parse = jobjType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (register is null || jobjType is null || parse is null || register.GetParameters().Length != 1)
            {
                throw new InvalidOperationException("PluginInterface.RegisterPage(JObject) introuvable");
            }

            // l'adresse commence par « / » : Plugin Pages y ajoute lui-même le chemin de base du serveur
            var payload = JsonSerializer.Serialize(new
            {
                Id = PageId,
                Url = "/RecoPlus/UserPage",
                DisplayText = "Reco+",
                Icon = "style",
                IsEnabledAssembly = typeof(PluginPagesIntegration).Assembly.FullName,
                IsEnabledClass = nameof(PluginPagesIntegration),
                IsEnabledMethod = nameof(IsEnabled),
            });
            register.Invoke(null, new[] { parse.Invoke(null, new object[] { payload }) });
            State = Texts.Pack("page « Reco+ » déclarée dans le menu des utilisateurs");
            _log.LogInformation("Reco+: user page registered with Plugin Pages");
        }
        catch (Exception ex) when (ex is TargetInvocationException or TargetException or InvalidOperationException or InvalidCastException or ArgumentException or MissingMethodException or MemberAccessException)
        {
            State = Texts.Pack("échec : {0}", (ex.InnerException ?? ex).Message);
            _log.LogWarning("Reco+: could not register the user page with Plugin Pages ({Msg})", (ex.InnerException ?? ex).Message);
        }
    }
}
