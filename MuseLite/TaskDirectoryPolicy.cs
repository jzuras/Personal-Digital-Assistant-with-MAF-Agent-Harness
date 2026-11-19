using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace MuseLite;

#pragma warning disable MAAI001 // FileAccessProvider (write-family tool names) is an experimental harness API.

/// <summary>
/// Decides whether a gated file-write call targets the working store's task directory, so an
/// unattended scheduled run can create and update its own task files without a human approval
/// prompt. Pure policy over a call's tool name and <c>fileName</c> argument — no framework state.
/// </summary>
internal static class TaskDirectoryPolicy
{
    // The directory in the working store that holds task files. Writes under it are auto-approved
    // (see IsTaskDirectoryWrite) so a scheduled, unattended task run — which has no human to answer an
    // approval prompt — can still create and update its own task files.
    private const string TaskDirectoryName = "tasks";

    // The write-family file_access tools whose single `fileName` argument is auto-approved when it
    // targets a file under the task directory. delete is deliberately excluded (a task run has no
    // reason to remove a task file; delete stays a human decision), and the read-only file tools are
    // already auto-approved by FileAccessProvider.ReadOnlyToolsAutoApprovalRule. The names come from
    // FileAccessProvider so they track the framework rather than hardcoded literals.
    private static HashSet<string> TaskWriteToolNames { get; } = new(StringComparer.Ordinal)
    {
        FileAccessProvider.WriteToolName,
        FileAccessProvider.ReplaceToolName,
        FileAccessProvider.ReplaceLinesToolName,
    };

    /// <summary>
    /// Determines whether a gated call is a write-family <c>file_access</c> tool
    /// (<see cref="FileAccessProvider.WriteToolName"/> / <see cref="FileAccessProvider.ReplaceToolName"/> /
    /// <see cref="FileAccessProvider.ReplaceLinesToolName"/>) whose target file is under the
    /// <c>tasks/</c> directory, so an unattended task run can create and update its own task files
    /// without a human approval prompt. Read-only file tools are auto-approved elsewhere; delete is
    /// excluded on purpose.
    /// </summary>
    /// <param name="call">The gated function call.</param>
    /// <returns><see langword="true"/> if the call writes a file under <c>tasks/</c>; otherwise
    /// <see langword="false"/>.</returns>
    internal static bool IsTaskDirectoryWrite(FunctionCallContent call)
    {
        if (TaskDirectoryPolicy.TaskWriteToolNames.Contains(call.Name) is false)
        {
            return false;
        }

        return TaskDirectoryPolicy.IsUnderTaskDirectory(TaskDirectoryPolicy.TryGetFileNameArgument(call.Arguments));
    }

    /// <summary>
    /// Extracts the <c>fileName</c> argument from a tool call's arguments, or <see langword="null"/>
    /// when absent or not a string. Handles both a plain string value and a JSON string element, since
    /// arguments may arrive either way depending on the invocation path.
    /// </summary>
    /// <param name="arguments">The call's arguments, or <see langword="null"/> when it has none.</param>
    /// <returns>The <c>fileName</c> value, or <see langword="null"/>.</returns>
    private static string? TryGetFileNameArgument(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            if (string.Equals(pair.Key, "fileName", StringComparison.Ordinal) is false)
            {
                continue;
            }

            return pair.Value switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Determines whether a model-supplied <c>fileName</c> resolves to a file under the task directory.
    /// The path is normalized to forward slashes and split into segments; any <c>..</c> segment is
    /// rejected outright (so a crafted name such as <c>tasks/../memories.md</c> can never be approved),
    /// <c>.</c> segments are ignored, and the first real segment must be the task directory with at
    /// least one more segment (a file) after it. The directory segment is matched case-insensitively
    /// because the filesystem backend treats <c>tasks/</c> and <c>Tasks/</c> as one directory and
    /// nothing sensitive lives adjacent under a case variant.
    /// </summary>
    /// <param name="fileName">The <c>fileName</c> argument, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the path is under the task directory; otherwise
    /// <see langword="false"/>.</returns>
    private static bool IsUnderTaskDirectory(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) is true)
        {
            return false;
        }

        string normalized = fileName.Replace('\\', '/').Trim();
        string[] rawSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<string>(rawSegments.Length);
        foreach (string segment in rawSegments)
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal) is true)
            {
                // Any parent-directory hop could escape the task directory — never auto-approve it.
                return false;
            }

            if (string.Equals(segment, ".", StringComparison.Ordinal) is false)
            {
                segments.Add(segment);
            }
        }

        return segments.Count >= 2
            && string.Equals(segments[0], TaskDirectoryPolicy.TaskDirectoryName, StringComparison.OrdinalIgnoreCase);
    }
}
