namespace Argon.Features.Web;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

/// <summary>
/// Restricting a role to its own controllers.
/// </summary>
/// <remarks>
/// MVC discovers controllers from application parts, which means the whole assembly: every role that
/// calls <c>AddControllers</c> maps every controller the product has, whether or not it registered
/// the services behind them. For the entry point that is the intent — the webhook and file endpoints
/// are its. For a role whose whole point is being small and exposed, it is a surface it never asked
/// for, and one whose handlers would fault on a service it does not have.
/// </remarks>
public static class ControllerScoping
{
    /// <summary>
    /// Keeps only the controllers declared under one of <paramref name="namespaces"/>.
    /// </summary>
    public static IMvcBuilder RestrictTo(this IMvcBuilder builder, params string[] namespaces)
        => builder.ConfigureApplicationPartManager(parts =>
            parts.FeatureProviders.Add(new NamespaceScopedControllers(namespaces)));

    private sealed class NamespaceScopedControllers(string[] namespaces) : IApplicationFeatureProvider<ControllerFeature>
    {
        /// <remarks>
        /// Runs after the default provider has filled the feature, so this only ever removes. That is
        /// what makes it composable with whatever else discovers controllers rather than a
        /// replacement for it.
        /// </remarks>
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            var outsiders = feature.Controllers
               .Where(controller => !namespaces.Any(allowed =>
                    controller.Namespace?.StartsWith(allowed, StringComparison.Ordinal) is true))
               .ToArray();

            foreach (var controller in outsiders)
                feature.Controllers.Remove(controller);
        }
    }
}
