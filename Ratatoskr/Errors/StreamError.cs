/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Ratatoskr <https://www.github.com/Vanaheimr/Ratatoskr>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Text.RegularExpressions;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// RFC 6120, section 4.9: a stream error is always final for the stream - the
/// server closes it immediately afterwards.
/// </summary>
/// <param name="Condition">
/// The defined condition from section 4.9.3, such as <c>conflict</c> or
/// <c>system-shutdown</c>.
/// </param>
/// <param name="Text">Optional text intended for humans.</param>
public sealed record StreamError(string   Condition,
                                 string?  Text = null)
{

    /// <summary>
    /// The namespace of the defined conditions.
    /// </summary>
    public const string Namespace = "urn:ietf:params:xml:ns:xmpp-streams";

    /// <summary>
    /// Is another connection attempt worth it?
    ///
    /// Only for conditions that describe a temporary situation. For everything
    /// else - wrong credentials, a displaced resource, an unknown host, a policy
    /// violation - a reconnect would produce the same error again and burden the
    /// server for nothing.
    ///
    /// <c>see-other-host</c> deliberately counts as non-retryable here: the
    /// server names a different address, and as long as that is not evaluated
    /// (RFC 6120, section 4.9.3.16), a reconnect against the same address would
    /// run into a loop.
    /// </summary>
    public bool IsRecoverable
        => Condition is "connection-timeout"
                     or "internal-server-error"
                     or "remote-connection-failed"
                     or "reset"
                     or "resource-constraint"
                     or "system-shutdown"
                     or "undefined-condition";

    /// <summary>
    /// Reads a <c>&lt;stream:error/&gt;</c> frame.
    /// </summary>
    /// <returns>False if the stanza is not a stream error.</returns>
    public static bool TryParse(string stanza, out StreamError? error)
    {

        error = null;

        // The prefix is not prescribed - stream: is customary, but any prefix
        // bound to the streams namespace is possible.
        if (!Regex.IsMatch(stanza, @"^\s*<(?:[a-zA-Z][\w\-]*:)?error\b"))
            return false;

        var condition = "undefined-condition";

        foreach (Match m in Regex.Matches(stanza,
                                          @"<([a-zA-Z][\w\-]*)\s[^>]*xmlns\s*=\s*['""]" +
                                          Regex.Escape(Namespace) + @"['""]"))
        {
            if (m.Groups[1].Value != "text")
            {
                condition = m.Groups[1].Value;
                break;
            }
        }

        var textMatch = Regex.Match(stanza, @"<text\b[^>]*>(.*?)</text\s*>", RegexOptions.Singleline);
        var text      = textMatch.Success ? textMatch.Groups[1].Value.Trim() : null;

        error = new StreamError(condition, string.IsNullOrEmpty(text) ? null : text);

        return true;

    }

    public override string ToString()
        => Text is null
               ? Condition
               : $"{Condition}: {Text}";

}
