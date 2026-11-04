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

using System.Collections.Concurrent;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;


/// <summary>
/// What is known about whose devices, and for how long it stays known.
/// </summary>
/// <remarks>
/// Every encrypted message asked the recipient's server anew which devices
/// they have - on the sending path, before anything went out, with the IQ
/// timeout as the upper bound. One measured conversation: three messages,
/// three times the same question to the same server, and the first of them
/// took ten seconds because the answer had to travel and the asking gave up
/// half a second before it arrived. The two after it came back in nineteen
/// milliseconds. Nothing had changed in between; only the first asking was
/// cold.
///
/// So the answer is kept. What makes that defensible is not the span it is
/// kept for but XEP-0163: whoever changes their device list publishes it, and
/// everybody subscribed is told without asking. <see cref="Remember"/> is
/// called from that notification as well, so the usual way an entry stops
/// being true is that the truth arrives by itself. The spans below are what
/// remains for the case where it does not - a contact whose notification was
/// missed, or one nobody is subscribed to.
///
/// <b>Three answers, three different lifetimes, and the shortest of them is
/// the dangerous one.</b> "They publish devices" can be held a while: it is
/// wrong only in the direction of a message a new device of theirs cannot
/// read, which that device's owner notices. "They publish nothing" is the
/// answer a caller turns into writing in the clear, so it is held for minutes
/// and not longer: someone who switches OMEMO on should not be written to
/// unencrypted for an hour afterwards because this side once asked too early.
/// And <see cref="PepLookup.Unknown"/> - nobody answered - is never kept at
/// all. It is not knowledge, it is its absence, and an absence that is stored
/// becomes an answer the next question never gets asked about.
///
/// <b>Nothing expired is served.</b> An entry past its time is as good as
/// absent here, and the asking happens again - including the chance that it
/// times out, and that the caller then refuses to write. Serving a stale list
/// rather than letting the question fail would turn whoever can hold up one
/// IQ into whoever decides which devices a message is encrypted to; keeping
/// that door shut costs a round trip now and then.
/// </remarks>
public sealed class OmemoDeviceLists
{

    #region Data

    /// <summary>
    /// How long a published list is believed without asking again.
    /// </summary>
    public static readonly TimeSpan DefaultPublishedFor  = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long "publishes nothing" is believed without asking again.
    /// </summary>
    public static readonly TimeSpan DefaultAbsentFor     = TimeSpan.FromMinutes(5);


    private readonly ConcurrentDictionary<JID, Entry>  entries  = new();
    private readonly Func<DateTimeOffset>              now;

    private sealed record Entry(OmemoDeviceList?  List,
                                PepLookup         Lookup,
                                DateTimeOffset    Until);

    #endregion

    #region Properties

    /// <summary>
    /// How long a published list is believed here.
    /// </summary>
    public TimeSpan PublishedFor    { get; }

    /// <summary>
    /// How long "publishes nothing" is believed here.
    /// </summary>
    public TimeSpan AbsentFor       { get; }

    /// <summary>
    /// How many addresses are currently known - for tests and diagnosis, and
    /// counting the expired ones among them.
    /// </summary>
    public Int32 Count
        => entries.Count;

    #endregion

    #region Constructor(s)

    /// <summary>
    /// Creates a new store of what is known about whose devices.
    /// </summary>
    /// <param name="PublishedFor">How long a published list is believed; the default is a quarter of an hour.</param>
    /// <param name="AbsentFor">How long "publishes nothing" is believed; the default is five minutes.</param>
    /// <param name="Clock">Where the time comes from. Handed in so that a test does not have to wait.</param>
    public OmemoDeviceLists(TimeSpan?              PublishedFor   = null,
                            TimeSpan?              AbsentFor      = null,
                            Func<DateTimeOffset>?  Clock          = null)
    {

        this.PublishedFor  = PublishedFor ?? DefaultPublishedFor;
        this.AbsentFor     = AbsentFor    ?? DefaultAbsentFor;
        this.now           = Clock        ?? (() => DateTimeOffset.UtcNow);

    }

    #endregion


    #region Get(BareJid)

    /// <summary>
    /// What is known about this address, or null where the question has to be
    /// asked.
    /// </summary>
    /// <param name="BareJid">The address, bare.</param>
    public (OmemoDeviceList? List, PepLookup Lookup)? Get(JID BareJid)
    {

        if (!entries.TryGetValue(BareJid.Bare, out var entry))
            return null;

        if (now() >= entry.Until)
        {
            // Taken out rather than left lying: an entry that is past its time
            // will not become valid again, and a store that only ever grows is
            // a leak with a long fuse.
            entries.TryRemove(BareJid.Bare, out _);
            return null;
        }

        return (entry.List, entry.Lookup);

    }

    #endregion

    #region Remember(BareJid, List, Lookup)

    /// <summary>
    /// Keeps what an asking - or a notification - produced.
    /// </summary>
    /// <remarks>
    /// <see cref="PepLookup.Unknown"/> changes nothing, and it does not clear
    /// what is there either. An IQ that went unanswered says nothing about the
    /// devices of its addressee, so it may neither become an answer nor
    /// unseat one.
    /// </remarks>
    /// <param name="BareJid">The address, bare.</param>
    /// <param name="List">The list, where there is one.</param>
    /// <param name="Lookup">What the asking produced.</param>
    public void Remember(JID                BareJid,
                         OmemoDeviceList?   List,
                         PepLookup          Lookup)
    {

        if (Lookup == PepLookup.Unknown)
            return;

        entries[BareJid.Bare] = new Entry(
                                    List,
                                    Lookup,
                                    now() + (Lookup == PepLookup.Published
                                                 ? PublishedFor
                                                 : AbsentFor)
                                );

    }

    #endregion

    #region Forget(BareJid)

    /// <summary>
    /// Drops what is known about one address, so that the next question is
    /// asked for real.
    /// </summary>
    /// <param name="BareJid">The address, bare.</param>
    public void Forget(JID BareJid)
        => entries.TryRemove(BareJid.Bare, out _);

    #endregion

    #region Clear()

    /// <summary>
    /// Drops everything.
    /// </summary>
    public void Clear()
        => entries.Clear();

    #endregion

}
