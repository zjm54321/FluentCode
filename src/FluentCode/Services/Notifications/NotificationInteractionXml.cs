using System;
using System.Xml.Linq;

namespace FluentCode.Services.Notifications;

/// <summary>Preserves mixed input order; the SDK builder groups textboxes before selections.</summary>
internal static class NotificationInteractionXml
{
    internal static Uri AppLogo => new(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "app-48.png"));
    internal static string Build(NotificationInteractionPlan plan)
    {
        if (plan.Inputs.Count > NotificationFormPolicy.MaxInputs || plan.Buttons.Count > NotificationFormPolicy.MaxButtons)
            throw new InvalidOperationException("Native interaction budget exceeded.");

        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        binding.Add(new XElement("image", new XAttribute("placement", "appLogoOverride"),
            new XAttribute("src", AppLogo.AbsoluteUri), new XAttribute("alt", "FluentCode")));
        foreach (string text in plan.TextElements) binding.Add(new XElement("text", text));
        var toast = new XElement("toast", new XAttribute("launch", Arguments(plan.BodyNonce)),
            new XElement("visual", binding));
        if (plan.Inputs.Count > 0 || plan.Buttons.Count > 0)
        {
            var actions = new XElement("actions");
            foreach (var input in plan.Inputs)
            {
                var element = new XElement("input", new XAttribute("id", input.Id),
                    new XAttribute("type", input.Choices is null ? "text" : "selection"),
                    new XAttribute("title", input.Title));
                if (input.Choices is { } choices)
                    foreach (var choice in choices)
                        element.Add(new XElement("selection", new XAttribute("id", choice.Id), new XAttribute("content", choice.Label)));
                else element.Add(new XAttribute("placeHolderContent", "请输入"));
                actions.Add(element);
            }
            foreach (var button in plan.Buttons)
                actions.Add(new XElement("action", new XAttribute("content", button.Label),
                    new XAttribute("arguments", Arguments(button.Nonce)), new XAttribute("activationType", "foreground")));
            toast.Add(actions);
        }
        // XElement escapes every text/attribute value. Never interpolate model/user text into markup.
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static string Arguments(string nonce) => NotificationInteractionCoordinator.NonceKey + "=" + nonce;
}
