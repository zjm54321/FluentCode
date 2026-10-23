using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class AttachmentPreparation
{
    public static async Task RunAsync()
    {
        var source = new byte[] { 1, 2, 3 };
        var encoded = OpenCodeAttachment.Encode(new[] { new OpenCodeAttachmentDraft("x.txt", null, source) });
        source[0] = 9;
        if (!ReferenceEquals(encoded, OpenCodeAttachment.ValidateFiles(encoded)) || encoded[0].Uri != "data:text/plain;base64,AQID")
            throw new InvalidOperationException("Immutable validated attachment must be reused, not decoded again.");
        var combined = OpenCodeAttachment.Combine(new[] { encoded, encoded });
        if (combined.Count != 2 || !ReferenceEquals(combined[0], encoded[0])
            || !ReferenceEquals(combined, OpenCodeAttachment.ValidateFiles(combined)))
            throw new InvalidOperationException("Draft batches must combine without repeating Base64 encoding or validation.");
        if (encoded is IList<PromptFileAttachment> list)
        {
            try { list[0] = new("file:///bad"); throw new InvalidOperationException("Validated list must reject mutation."); }
            catch (NotSupportedException) { }
        }

        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            if (body.RootElement.GetProperty("files")[0].GetProperty("uri").GetString() != encoded[0].Uri)
                throw new InvalidOperationException("Transport must use the validated snapshot.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"id\":\"msg_ack\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"payload\":{\"text\":\"ok\"}}}", Encoding.UTF8, "application/json") };
        }));
        using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid" }, true);
        var watched = new WatchedFiles(encoded);
        var ui = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task<OpenCodeInboxUser> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(ui);
            pending = client.SendPromptGuardedAsync("ses_test", "hello", watched, action =>
            {
                if (!watched.ReadOffUi) throw new InvalidOperationException("Validation must finish before the final ownership check.");
                return false;
            }, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try { await pending; throw new InvalidOperationException("Revoked source must not start transport."); }
        catch (OperationCanceledException) { }
        if (calls != 0 || !watched.ReadOffUi) throw new InvalidOperationException("Heavy validation must run off the UI context before source-owned initiation.");
        await client.SendPromptAsync("ses_test", "hello", encoded, default);
        if (calls != 1) throw new InvalidOperationException("Exactly one transport initiation is required.");
        Console.WriteLine("PASS attachment preparation: immutable reuse/combination, worker validation, final revoked-source check, exact wire (7 assertions).");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class WatchedFiles(IReadOnlyList<PromptFileAttachment> files) : IReadOnlyList<PromptFileAttachment>
    {
        public bool ReadOffUi { get; private set; }
        public int Count => files.Count;
        public PromptFileAttachment this[int index]
        {
            get
            {
                if (SynchronizationContext.Current is not null) throw new InvalidOperationException("Attachment validation ran on UI context.");
                ReadOffUi = true;
                return files[index];
            }
        }
        public IEnumerator<PromptFileAttachment> GetEnumerator() => files.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
