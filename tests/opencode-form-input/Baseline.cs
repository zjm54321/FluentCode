using System;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Baseline
{
    private static int Main()
    {
        var form = JsonSerializer.Deserialize<OpenCodeFormInfo>(Fixtures.DescribedChoice, OpenCodeJson.Options)!;
        var accepted = OpenCodeFormInputPolicy.TryCreatePlan(form, out var plan, out var reasons);
        Console.WriteLine($"Baseline accepted={accepted}, reasons={string.Join("; ", reasons)}");
        Console.WriteLine($"Fixture fingerprint: {OpenCodeInteractionFingerprint.ForForm(form)}");
        var descriptionOnly = JsonSerializer.Deserialize<OpenCodeFormInfo>(
            """{"fields":[{"key":"q","type":"string","options":[{"value":"yes","label":"Yes","description":"Ordinary choice"}]}]}""", OpenCodeJson.Options)!;
        var descriptionAccepted = OpenCodeFormInputPolicy.TryCreatePlan(descriptionOnly, out _, out var descriptionReasons);
        Console.WriteLine($"Baseline described-option accepted={descriptionAccepted}, reasons={string.Join("; ", descriptionReasons)}");
        if (accepted || plan is not null || reasons.Count == 0 || descriptionAccepted || descriptionReasons.Count == 0)
            throw new Exception("Original rejection was not reproduced.");
        Console.WriteLine("PASS baseline: original production sources reject standard metadata and option description");
        return 0;
    }
}
