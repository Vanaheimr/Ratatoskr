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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Writing a file that only its owner may read.
/// </summary>
/// <remarks>
/// Two files in this library hold key material next to the program that uses
/// it: the account store of the server and the OMEMO store of the client.
/// Neither holds a password - the one keeps derived SCRAM keys, the other
/// ratchet state - and for what they protect that makes no difference. Whoever
/// reads the server's StoredKey can answer any SCRAM challenge for that
/// account; whoever reads the OMEMO store can read the conversations.
///
/// <b>The mode goes on at creation and not afterwards.</b> Creating a file
/// readable and restricting it once the content is in leaves a window, and the
/// window is exactly as long as the writing.
///
/// On Windows there is no mode to set. Permissions there are ACLs and are
/// inherited from the directory, so this falls back to an ordinary write -
/// whoever runs it there has to put the file somewhere that already suits it.
/// Saying so is better than a call that quietly does nothing.
/// </remarks>
internal static class OwnerOnlyFile
{

    #region Write(Path, Content)

    /// <summary>
    /// Writes a file readable and writable by its owner alone (0600 on Unix).
    /// </summary>
    public static void Write(String Path, String Content)
    {

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path, Content);
            return;
        }

        using var stream = File.Open(Path,
                                     new FileStreamOptions {
                                         Mode            = FileMode.Create,
                                         Access          = FileAccess.Write,
                                         UnixCreateMode  = UnixFileMode.UserRead | UnixFileMode.UserWrite
                                     });

        using var writer = new StreamWriter(stream);

        writer.Write(Content);

    }

    #endregion

}
