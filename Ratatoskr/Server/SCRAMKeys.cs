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

namespace org.GraphDefined.Vanaheimr.Ratatoskr.Server
{

    /// <summary>
    /// The two keys a server keeps per account and SCRAM mechanism (RFC 5802,
    /// section 3).
    /// </summary>
    /// <remarks>
    /// Deliberately not the password and not the <c>ClientKey</c> either: from
    /// <see cref="StoredKey"/> the <c>ClientKey</c> cannot be computed back,
    /// but it can be checked whether the client knows it. Whoever captures the
    /// database of a server can therefore not readily log in as the user with
    /// it - that is precisely the point of the construction.
    ///
    /// The <see cref="ServerKey"/>, by contrast, has to be kept, because with
    /// it the server proves to the client that it knows the password as well
    /// (section 5, <c>ServerSignature</c>).
    /// </remarks>
    /// <param name="StoredKey">H(HMAC(SaltedPassword, "Client Key")).</param>
    /// <param name="ServerKey">HMAC(SaltedPassword, "Server Key").</param>
    public sealed record SCRAMKeys(Byte[] StoredKey,
                                   Byte[] ServerKey);

}
