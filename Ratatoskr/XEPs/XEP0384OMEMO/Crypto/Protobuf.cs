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
/// As much of Protocol Buffers as OMEMO needs - varints and length-delimited
/// fields, no more.
/// </summary>
/// <remarks>
/// <b>Why by hand and not with a library.</b> What is needed are three message
/// kinds with eleven fields between them, all of type <c>uint32</c> or
/// <c>bytes</c>. To hang a code generator and its toolchain into the building
/// for that costs more than it carries - and the encoding itself is
/// exhaustively described in forty lines.
///
/// <b>The actual reason, though, is another one:</b> these bytes go into the
/// associated data of the encryption (XEP-0384, section 4.3:
/// <c>ad ‖ OMEMOMessage.proto(header)</c>). The encoding therefore has to be
/// <b>reproducible bit for bit</b> - both sides have to form the same bytes
/// from the same header, otherwise every check fails. A library that reorders
/// fields, leaves out default values or pads varints differently would be no
/// convenience here but a source of errors nobody sees.
///
/// That is why this encoder always writes all fields, always in the order of
/// their numbers and never with padded varints.
/// </remarks>
public static class Protobuf
{

    #region Writing

    /// <summary>
    /// A varint (base 128, least significant group first, top bit as the
    /// continuation marker).
    /// </summary>
    public static void WriteVarint(List<Byte> target, UInt64 value)
    {

        while (value >= 0x80)
        {
            target.Add((Byte) (value | 0x80));
            value >>= 7;
        }

        target.Add((Byte) value);

    }

    /// <summary>
    /// A field of type <c>uint32</c> (wire type 0).
    /// </summary>
    public static void WriteUInt32(List<Byte> target, Int32 fieldNumber, UInt32 value)
    {
        WriteVarint(target, (UInt64) fieldNumber << 3 | 0);
        WriteVarint(target, value);
    }

    /// <summary>
    /// A field of type <c>bytes</c> (wire type 2): length, then content.
    /// </summary>
    public static void WriteBytes(List<Byte> target, Int32 fieldNumber, Byte[] value)
    {

        WriteVarint(target, (UInt64) fieldNumber << 3 | 2);
        WriteVarint(target, (UInt64) value.Length);

        target.AddRange(value);

    }

    #endregion

    #region Reading

    /// <summary>
    /// Reads the fields of a message in the order in which they stand there.
    /// </summary>
    /// <returns>
    /// Field number, wire type and the raw value: with wire type 0 the number,
    /// with wire type 2 the bytes.
    /// </returns>
    /// <remarks>
    /// Unknown field numbers are skipped and not refused - that is how Protocol
    /// Buffers wants it, and that is how a later version of the specification
    /// stays readable. An <b>unknown wire type</b>, by contrast, is an abort:
    /// from there on it is no longer recognisable where the next field begins,
    /// and what would be read after it would be guessed.
    /// </remarks>
    public static IEnumerable<(Int32 Field, Int32 WireType, UInt64 Number, Byte[] Data)> Read(Byte[] data)
    {

        var i = 0;

        while (i < data.Length)
        {

            var key    = ReadVarint(data, ref i);
            var field  = (Int32) (key >> 3);
            var type   = (Int32) (key & 7);

            switch (type)
            {

                case 0:
                    yield return (field, type, ReadVarint(data, ref i), []);
                    break;

                case 2:
                    var length = (Int32) ReadVarint(data, ref i);

                    if (length < 0 || i + length > data.Length)
                        throw new FormatException("A length-delimited field reaches past the end.");

                    yield return (field, type, 0, data[i..(i + length)]);
                    i += length;
                    break;

                default:
                    throw new FormatException(
                              $"Wire type {type} does not occur in OMEMO; from here on it is no longer " +
                              "recognisable where the next field begins.");

            }

        }

    }

    /// <summary>
    /// Reads a varint and moves the read pointer on.
    /// </summary>
    public static UInt64 ReadVarint(Byte[] data, ref Int32 i)
    {

        UInt64  value  = 0;
        var     shift  = 0;

        while (true)
        {

            if (i >= data.Length)
                throw new FormatException("The varint ends before its last byte.");

            // Ten groups of seven bits are seventy - more than fit into a
            // UInt64. Without this limit one could read arbitrarily far past
            // the value with a chain of continuation bytes.
            if (shift > 63)
                throw new FormatException("The varint is longer than a 64-bit value can be.");

            var b = data[i++];

            value |= (UInt64) (b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return value;

            shift += 7;

        }

    }

    #endregion

}
