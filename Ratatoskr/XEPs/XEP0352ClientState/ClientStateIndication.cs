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

using System.Xml;
using System.Xml.Linq;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// What happens to a stanza as long as the client has declared itself inactive
/// (XEP-0352, section 3).
/// </summary>
public enum ClientStateHandling
{

    /// <summary>
    /// Goes out at once - the state of the client changes nothing about that.
    /// </summary>
    Immediately,

    /// <summary>
    /// Is held back and delivered afterwards at the <c>&lt;active/&gt;</c>.
    /// </summary>
    Queued,

    /// <summary>
    /// Is dropped and never arrives.
    /// </summary>
    Discarded

}

/// <summary>
/// XEP-0352: Client State Indication - the client says whether a human being is
/// looking.
/// </summary>
/// <remarks>
/// Two nonzas, no answer (section 4.2: "There is no reply from the server to
/// either of these elements"), and the server may thereupon hold traffic back.
/// The point is not thrift on the wire: a radio modem that wakes up for every
/// presence change empties the battery of a telephone lying in a pocket.
///
/// <b>What may be held back is decided by the server</b> - the specification
/// names only examples in section 3. This class holds the decision fast in one
/// place and answers it as a pure function, so that it is checkable on its own
/// and does not vanish into the send loop of the session.
///
/// The guideline behind it: <b>what is held back is only what is still true
/// later.</b> A presence from before can be superseded, but is not wrong - the
/// last one holds. A "is typing" from before is, after the delivery, simply a
/// lie; that is why it is dropped and not kept (section 3: "Discard messages
/// containing only Chat State Notifications … payloads"). And everything a
/// sender is waiting on goes out at once.
/// </remarks>
public static class ClientStateIndication
{

    /// <summary>
    /// The namespace of XEP-0352.
    /// </summary>
    public const String Namespace    = "urn:xmpp:csi:0";

    /// <summary>
    /// The announcement among the stream features (section 4.1).
    /// </summary>
    public const String FeatureXml   = $"<csi xmlns='{Namespace}'/>";

    /// <summary>
    /// "Somebody is looking again."
    /// </summary>
    public const String ActiveXml    = $"<active xmlns='{Namespace}'/>";

    /// <summary>
    /// "The device is lying in the pocket."
    /// </summary>
    public const String InactiveXml  = $"<inactive xmlns='{Namespace}'/>";

    #region HandlingOf(stanza)

    /// <summary>
    /// How this stanza is to be dealt with as long as the client is inactive.
    /// </summary>
    /// <remarks>
    /// <b>Nonzas and <c>iq</c> go out at once.</b> An <c>&lt;a/&gt;</c> or a
    /// stream error does not belong to the traffic a telephone would like to
    /// postpone but to the stream itself. And an <c>iq</c> is a question with a
    /// deadline: whoever holds it back lets the time run out at the sender and
    /// answers it afterwards - the answer then comes to a question nobody is
    /// asking any more.
    ///
    /// <b>Errors go out at once</b>, in both directions and for both kinds of
    /// stanza: an error is the answer to something the client sent itself.
    ///
    /// <b>A message with text is the reason the device rings.</b> To hold it
    /// back would mean turning a saving of traffic into a delay of delivery -
    /// and that is precisely not what XEP-0352 is there for.
    ///
    /// A <c>&lt;body/&gt;</c> of nothing but spaces does not count as text. The
    /// other way round, every message would count as important that carries an
    /// empty <c>&lt;body/&gt;</c> beside its chat states - and clients do that
    /// in fact.
    /// </remarks>
    public static ClientStateHandling HandlingOf(String stanza)
    {

        var name = StanzaElement.NameOf(stanza);

        // Everything else - iq and every nonza - is not postponable.
        if (name is not ("message" or "presence"))
            return ClientStateHandling.Immediately;

        XElement element;

        try
        {
            element = XElement.Parse(stanza, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            // What cannot be read is not held back. A buffer is the worst place
            // for something not understood: it would come out later, and nobody
            // would know then why.
            return ClientStateHandling.Immediately;
        }

        var type = element.Attr("type");

        if (type == "error")
            return ClientStateHandling.Immediately;

        // Presence: the presence itself is postponable, the question about it is
        // not. A <presence type='subscribe'/> waits for a decision of the human
        // being and is thereby the same as a message - RFC 6121, section 3.1.3.
        if (name == "presence")
            return type is "subscribe" or "subscribed" or "unsubscribe" or "unsubscribed"
                       ? ClientStateHandling.Immediately
                       : ClientStateHandling.Queued;

        if (!String.IsNullOrWhiteSpace(element.Elements()
                                              .FirstOrDefault(e => e.Name.LocalName == "body")
                                              ?.Value))
            return ClientStateHandling.Immediately;

        // What is counted are only the extensions, that is, the children in a
        // namespace other than the stanza itself. <thread/> stands in the
        // namespace of the stanza and belongs to no extension; whoever counted
        // it would take every chat state message with a thread for a message
        // with content - and XEP-0085 recommends precisely this combination.
        var extensions = element.Elements()
                                   .Where(e => e.Name.NamespaceName != element.Name.NamespaceName)
                                   .ToList();

        if (extensions.Count > 0 &&
            extensions.All(e => e.Name.NamespaceName == ChatStateExtensions.Namespace))
            return ClientStateHandling.Discarded;

        return ClientStateHandling.Queued;

    }

    #endregion

    #region SupersedeKey(stanza)

    /// <summary>
    /// By what this held-back stanza is superseded from a later one - or null
    /// when it is superseded by nothing.
    /// </summary>
    /// <remarks>
    /// Section 3 names it as the first measure: "Suppress presence updates until
    /// the client becomes active again. On becoming active, push the
    /// <b>latest</b> presence from each contact." A contact who switches five
    /// times between "here" and "away" in ten minutes thereby leaves behind one
    /// presence and not five.
    ///
    /// The key is the full JID of the sender and not their bare JID: two devices
    /// of the same human being are two presences, and the one must not displace
    /// the other - otherwise their telephone would vanish from the list because
    /// their computer has signed off.
    ///
    /// Displacement happens only among equals: a sign-off supersedes a sign-on
    /// and the other way round, for both answer the same question. What
    /// <see cref="HandlingOf"/> gives out at once anyway does not even arrive
    /// here.
    /// </remarks>
    public static String? SupersedeKey(String stanza)
    {

        if (!StanzaElement.Is(stanza, "presence") ||
            HandlingOf(stanza) != ClientStateHandling.Queued)
            return null;

        String? from;

        try
        {
            from = XElement.Parse(stanza, LoadOptions.PreserveWhitespace).Attr("from");
        }
        catch (XmlException)
        {
            return null;
        }

        return from is null
                   ? null
                   : $"presence {from}";

    }

    #endregion

}
