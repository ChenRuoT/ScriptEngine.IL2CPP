using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ScriptEngineIl2Cpp.Triggers;

/// <summary>status.json / 命令结果的序列化。</summary>
internal static class StatusJson
{
    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Status(ScriptHost host, string scriptsDirectory)
    {
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("engine", $"ScriptEngine.IL2CPP {ReleaseVersion.Value}");
            writer.WriteNumber("processId", Environment.ProcessId);
            writer.WriteString("updatedUtc", DateTime.UtcNow.ToString("O"));
            writer.WriteString("scriptsDirectory", scriptsDirectory);
            writer.WriteNumber("generation", host.Generation);

            writer.WriteStartArray("plugins");
            foreach (var plugin in host.LoadedPlugins) WritePlugin(writer, plugin);
            writer.WriteEndArray();

            writer.WriteStartArray("contexts");
            foreach (var (generation, state) in host.ContextHistory.TakeLast(20))
            {
                writer.WriteStartObject();
                writer.WriteNumber("generation", generation);
                writer.WriteString("state", state);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("lastReload");
            if (host.LastReport is { } report) WriteReport(writer, report);
            else writer.WriteNullValue();

            writer.WriteEndObject();
        });
    }

    public static string CommandResult(string id, string command, bool ok, ReloadReport? report, ScriptHost host, string scriptsDirectory)
    {
        return Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("command", command);
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("generation", host.Generation);
            writer.WriteString("finishedUtc", DateTime.UtcNow.ToString("O"));

            writer.WriteStartArray("plugins");
            foreach (var plugin in host.LoadedPlugins) WritePlugin(writer, plugin);
            writer.WriteEndArray();

            writer.WritePropertyName("report");
            if (report is not null) WriteReport(writer, report);
            else writer.WriteNullValue();

            writer.WriteEndObject();
        });
    }

    private static void WriteReport(Utf8JsonWriter writer, ReloadReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("trigger", report.Trigger);
        writer.WriteBoolean("ok", report.Ok);
        writer.WriteBoolean("deferred", report.Deferred);
        writer.WriteBoolean("skipped", report.Skipped);
        writer.WriteNumber("generation", report.Generation);
        writer.WriteString("startedUtc", report.StartedUtc.ToString("O"));
        writer.WriteNumber("durationMs", report.DurationMs);
        writer.WriteNumber("prepareMs", report.PrepareMs);
        writer.WriteNumber("unloadMs", report.UnloadMs);
        writer.WriteNumber("loadMs", report.LoadMs);
        writer.WriteNumber("sweptObjects", report.SweptObjects);

        writer.WriteStartArray("loaded");
        foreach (var plugin in report.Loaded) WritePlugin(writer, plugin);
        writer.WriteEndArray();

        writer.WriteStartArray("unloaded");
        foreach (var plugin in report.Unloaded) WritePlugin(writer, plugin);
        writer.WriteEndArray();

        WriteStrings(writer, "infos", report.Infos);
        WriteStrings(writer, "warnings", report.Warnings);
        WriteStrings(writer, "errors", report.Errors);
        writer.WriteEndObject();
    }

    private static void WritePlugin(Utf8JsonWriter writer, PluginRecord plugin)
    {
        writer.WriteStartObject();
        writer.WriteString("guid", plugin.Guid);
        writer.WriteString("name", plugin.Name);
        writer.WriteString("version", plugin.Version);
        writer.WriteString("type", plugin.TypeName);
        writer.WriteString("file", plugin.File);
        if (plugin.UnloadReturned is { } returned) writer.WriteBoolean("unloadReturned", returned);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options)) write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
