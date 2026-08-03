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

using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0085: Serialisation and parsing of chat states.
/// </summary>
public static class ChatStateExtensions
{

    /// <summary>The namespace of XEP-0085.</summary>
    public const string Namespace = "http://jabber.org/protocol/chatstates";

    public static string ToXml(this ChatState state) => state switch
    {
        ChatState.Active    => $"<active xmlns='{Namespace}'/>",
        ChatState.Composing => $"<composing xmlns='{Namespace}'/>",
        ChatState.Paused    => $"<paused xmlns='{Namespace}'/>",
        ChatState.Inactive  => $"<inactive xmlns='{Namespace}'/>",
        ChatState.Gone      => $"<gone xmlns='{Namespace}'/>",
        _ => ""
    };

    /// <summary>
    /// Reads the chat state out of a message.
    ///
    /// What is sought are only the direct child elements and only those in the
    /// namespace of XEP-0085. The earlier check <c>Contains("&lt;composing")</c>
    /// did neither: it reported every element of the same name from any
    /// extension as a chat state, and the state of a message forwarded per
    /// XEP-0297 took effect outwardly.
    /// </summary>
    public static ChatState? ParseChatState(XElement message)
    {

        foreach (var child in message.Elements().Where(e => e.Name.NamespaceName == Namespace))
        {

            switch (child.Name.LocalName)
            {
                case "active":     return ChatState.Active;
                case "composing":  return ChatState.Composing;
                case "paused":     return ChatState.Paused;
                case "inactive":   return ChatState.Inactive;
                case "gone":       return ChatState.Gone;
            }

        }

        return null;

    }

}
