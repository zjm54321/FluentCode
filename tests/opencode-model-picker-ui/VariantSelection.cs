// Only native page/environment boundaries are doubled. SetBlankVariantAsync and its
// immutable token/preferences contracts are extracted verbatim from production by run.ps1.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private bool _blankPickerPending, _newSessionCreatePending, _blankModelChosen;
    private bool _ownsHome = true;
    private long _blankPickerEpoch;
    private string? _blankPreferenceError;
    private OpenCodeBlankDraftPreferences? _blankPreferences;
    private readonly object? _source = new();
    private readonly FakeClient? _client = new();
    private readonly CancellationTokenSource? _homeCancellation = new();
    private IReadOnlyList<OpenCodeModelInfo> _blankModels = Array.Empty<OpenCodeModelInfo>();
    private OpenCodeComposerToken _token;
    private int _labels;

    private bool OwnsHome(object source, FakeClient client, string runtime) => _ownsHome && ReferenceEquals(_source, source) && ReferenceEquals(_client, client) && runtime == "source";
    private bool OwnsBlankPreferences(OpenCodeBlankDraftPreferences basis, OpenCodeComposerToken token) => basis == _blankPreferences && _token == token;
    private void OwnBlankMetadata(OpenCodeBlankDraftPreferences basis) { }
    private void RefreshBlankPreferenceLabels() => _labels++;

    private sealed class FakeClient
    {
        internal Func<Task<OpenCodeModelsResponse>> Read = () => throw new Exception("Unset fake response");
        internal int Reads;
        internal Task<OpenCodeModelsResponse> ListModelsAsync(string directory, CancellationToken ct) { Reads++; return Read(); }
    }

    internal static async Task RunVariantSelectionChecks(Action<bool, string> check)
    {
        OpenCodePage Fixture()
        {
            var page = new OpenCodePage();
            page._token = new(1, Guid.NewGuid(), 1);
            page._blankPreferences = new("source", page._token.Draft, "/project", Model: new() { Id = "same", ProviderId = "a", Variant = "high" });
            page._client!.Read = () => Task.FromResult(new OpenCodeModelsResponse { Location = new() { Directory = "/project" },
                Data = new() { new() { Id = "same", ProviderId = "a", Variants = new() { new() { Id = "extra_high" } } },
                    new() { Id = "same", ProviderId = "b", Variants = new() { new() { Id = "other-provider" } } } } });
            return page;
        }
        var catalog = new OpenCodeModelInfo { Id = "same", ProviderId = "a", Variants = new() { new() { Id = "low" }, new() { Id = "extra_high" }, new() { Id = "Opaque/V2" }, new() { Id = "low" } } };
        var entries = BlankVariantMenuEntries(catalog, "extra_high");
        check(entries.Select(e => e.Id).SequenceEqual(new string?[] { null, "low", "extra_high", "Opaque/V2" }), "radio entries: default null first, then exact catalog raw IDs once each");
        check(entries.Select(e => e.Label).SequenceEqual(new[] { "默认", "低", "极高", "Opaque/V2" }), "radio labels are localized display only");
        check(entries.Count(e => e.IsChecked) == 1 && entries.Single(e => e.IsChecked).Id == "extra_high", "exactly the current raw variant is checked");
        check(BlankVariantMenuEntries(catalog, null).Single(e => e.IsChecked).Id is null, "no variant checks the default (null) radio item");
        check(!BlankVariantMenuEntries(catalog, "xhigh").Any(e => e.IsChecked), "synonym/unoffered current variant is not mapped onto an offered radio item");
        check(!BlankVariantMenuEntries(catalog, "extra_high").Any(e => e.Id == "default") && BlankVariantRadioGroup.Length > 0, "default is null, never a fabricated \"default\" wire ID; single group name");
        var p = Fixture();
        var initial = p._blankPreferences;
        foreach (var entry in BlankVariantMenuEntries(catalog, initial!.Model!.Variant)) { _ = entry; }
        check(p._blankPreferences == initial && p._client!.Reads == 0 && p._labels == 0, "building/seeding radio items does not read or commit");
        await p.SetBlankVariantAsync(p._blankPreferences!, p._token, "extra_high");
        check(p._blankPreferences!.Model?.Variant == "extra_high" && p._blankPreferences.Model.ProviderId == "a", "real page variant selection keeps exact ID/provider after fresh catalog");
        check(p._client!.Reads == 1 && p._blankModelChosen && !p._blankPickerPending && p._labels == 2 && p._blankModels.Count == 2, "variant commits fresh metadata and settles pending chrome");
        p = Fixture();
        await p.SetBlankVariantAsync(p._blankPreferences!, p._token, null);
        check(p._blankPreferences!.Model?.Variant is null && p._blankPreferences.Version == 1, "real page default clears variant to null");
        foreach (var invalid in new[] { "xhigh", "极高", "other-provider" })
        {
            p = Fixture();
            var basis = p._blankPreferences;
            await p.SetBlankVariantAsync(basis!, p._token, invalid);
            check(p._blankPreferences == basis && p._blankPreferenceError is not null && !p._blankPickerPending, "real page rejects unoffered/synonymous/localized/other-provider variant: " + invalid);
        }
        p = Fixture();
        p._client!.Read = () => Task.FromResult(new OpenCodeModelsResponse { Location = new() { Directory = "/project" }, Data = new() { new() { Id = "same", ProviderId = "a", Enabled = false } } });
        var original = p._blankPreferences;
        await p.SetBlankVariantAsync(original!, p._token, null);
        check(p._blankPreferences == original && p._blankPreferenceError is not null, "real page default cannot resurrect disabled model");
        p = Fixture();
        p._client!.Read = () => throw new InvalidOperationException("synthetic read failure");
        original = p._blankPreferences;
        await p.SetBlankVariantAsync(original!, p._token, "extra_high");
        check(p._blankPreferences == original && p._blankPreferenceError is not null && !p._blankPickerPending, "read failure leaves draft selection unchanged and releases pending");
        p = Fixture();
        var pending = new TaskCompletionSource<OpenCodeModelsResponse>();
        p._client!.Read = () => pending.Task;
        original = p._blankPreferences;
        var operation = p.SetBlankVariantAsync(original!, p._token, null);
        p._ownsHome = false;
        pending.SetResult(new() { Location = new() { Directory = "/project" }, Data = new() { new() { Id = "same", ProviderId = "a" } } });
        await operation;
        check(p._blankPreferences == original && !p._blankPickerPending, "late response after source loss cannot commit");
        p = Fixture();
        p._newSessionCreatePending = true;
        await p.SetBlankVariantAsync(p._blankPreferences!, p._token, null);
        check(p._client!.Reads == 0, "creation pending blocks variant read/mutation");
    }
}
