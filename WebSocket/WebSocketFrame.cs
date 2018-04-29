﻿#region Related components
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets.Implementation
{
	internal class WebSocketFrame
	{
		public bool IsFinBitSet { get; private set; }

		public WebSocketOpCode OpCode { get; private set; }

		public int Count { get; private set; }

		public WebSocketCloseStatus? CloseStatus { get; private set; }

		public string CloseStatusDescription { get; private set; }

		public WebSocketFrame(bool isFinBitSet, WebSocketOpCode webSocketOpCode, int count)
		{
			this.IsFinBitSet = isFinBitSet;
			this.OpCode = webSocketOpCode;
			this.Count = count;
		}

		public WebSocketFrame(bool isFinBitSet, WebSocketOpCode webSocketOpCode, int count, WebSocketCloseStatus closeStatus, string closeStatusDescription) : this(isFinBitSet, webSocketOpCode, count)
		{
			this.CloseStatus = closeStatus;
			this.CloseStatusDescription = closeStatusDescription;
		}

		public const int MaskKeyLength = 4;

		/// <summary>
		/// Mutate payload with the mask key. This is a reversible process, if you apply this to masked data it will be unmasked and visa versa.
		/// </summary>
		/// <param name="maskKey">The 4 byte mask key</param>
		/// <param name="payload">The payload to mutate</param>
		public static void ToggleMask(ArraySegment<byte> maskKey, ArraySegment<byte> payload)
		{
			if (maskKey.Count != WebSocketFrame.MaskKeyLength)
				throw new Exception($"MaskKey key must be {WebSocketFrame.MaskKeyLength} bytes");

			var buffer = payload.Array;
			var maskKeyArray = maskKey.Array;

			// apply the mask key (this is a reversible process so no need to copy the payload)
			for (var index = payload.Offset; index < payload.Count; index++)
			{
				int payloadIndex = index - payload.Offset; // index should start at zero
				int maskKeyIndex = maskKey.Offset + (payloadIndex % WebSocketFrame.MaskKeyLength);
				buffer[index] = (Byte)(buffer[index] ^ maskKeyArray[maskKeyIndex]);
			}
		}

		/// <summary>
		/// Read a WebSocket frame from the stream
		/// </summary>
		/// <param name="fromStream">The stream to read from</param>
		/// <param name="intoBuffer">The buffer to read into</param>
		/// <param name="cancellationToken">the cancellation token</param>
		/// <returns>A websocket frame</returns>
		public static async Task<WebSocketFrame> ReadAsync(Stream fromStream, ArraySegment<byte> intoBuffer, CancellationToken cancellationToken)
		{
			// allocate a small buffer to read small chunks of data from the stream
			var smallBuffer = new ArraySegment<byte>(new byte[8]);

			await WebSocketFrame.ReadExactlyAsync(2, fromStream, smallBuffer, cancellationToken).ConfigureAwait(false);
			byte byte1 = smallBuffer.Array[0];
			byte byte2 = smallBuffer.Array[1];

			// process first byte
			byte finBitFlag = 0x80;
			byte opCodeFlag = 0x0F;
			bool isFinBitSet = (byte1 & finBitFlag) == finBitFlag;
			var opCode = (WebSocketOpCode)(byte1 & opCodeFlag);

			// read and process second byte
			byte maskFlag = 0x80;
			bool isMaskBitSet = (byte2 & maskFlag) == maskFlag;
			uint length = await WebSocketFrame.ReadLengthAsync(byte2, smallBuffer, fromStream, cancellationToken).ConfigureAwait(false);
			int count = (int)length;

			// use the masking key to decode the data if needed
			if (isMaskBitSet)
			{
				var maskKey = new ArraySegment<byte>(smallBuffer.Array, 0, WebSocketFrame.MaskKeyLength);
				await WebSocketFrame.ReadExactlyAsync(maskKey.Count, fromStream, maskKey, cancellationToken).ConfigureAwait(false);
				await WebSocketFrame.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);
				var payloadToMask = new ArraySegment<byte>(intoBuffer.Array, intoBuffer.Offset, count);
				WebSocketFrame.ToggleMask(maskKey, payloadToMask);
			}
			else
				await WebSocketFrame.ReadExactlyAsync(count, fromStream, intoBuffer, cancellationToken).ConfigureAwait(false);

			return opCode == WebSocketOpCode.ConnectionClose
				? WebSocketFrame.DecodeCloseFrame(isFinBitSet, opCode, count, intoBuffer)
				: new WebSocketFrame(isFinBitSet, opCode, count); // note that by this point the payload will be populated
		}

		/// <summary>
		/// Extracts close status and close description information from the web socket frame
		/// </summary>
		/// <param name="isFinBitSet"></param>
		/// <param name="opCode"></param>
		/// <param name="count"></param>
		/// <param name="buffer"></param>
		/// <returns></returns>
		public static WebSocketFrame DecodeCloseFrame(bool isFinBitSet, WebSocketOpCode opCode, int count, ArraySegment<byte> buffer)
		{
			WebSocketCloseStatus closeStatus;
			string closeStatusDescription;

			if (count >= 2)
			{
				Array.Reverse(buffer.Array, buffer.Offset, 2); // network byte order
				var closeStatusCode = (int)BitConverter.ToUInt16(buffer.Array, buffer.Offset);
				closeStatus = Enum.IsDefined(typeof(WebSocketCloseStatus), closeStatusCode)
					? (WebSocketCloseStatus)closeStatusCode
					: WebSocketCloseStatus.Empty;

				int offset = buffer.Offset + 2;
				int descCount = count - 2;

				closeStatusDescription = descCount > 0
					? Encoding.UTF8.GetString(buffer.Array, offset, descCount)
					: null;
			}
			else
			{
				closeStatus = WebSocketCloseStatus.Empty;
				closeStatusDescription = null;
			}

			return new WebSocketFrame(isFinBitSet, opCode, count, closeStatus, closeStatusDescription);
		}

		/// <summary>
		/// Reads the length of the payload according to the contents of byte2
		/// </summary>
		/// <param name="byte2"></param>
		/// <param name="smallBuffer"></param>
		/// <param name="fromStream"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<uint> ReadLengthAsync(byte byte2, ArraySegment<byte> smallBuffer, Stream fromStream, CancellationToken cancellationToken = default(CancellationToken))
		{
			byte payloadLengthFlag = 0x7F;
			var length = (uint)(byte2 & payloadLengthFlag);

			// read a short length or a long length depending on the value of len
			if (length == 126)
				length = await WebSocketFrame.ReadUShortExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);

			else if (length == 127)
			{
				length = (uint)await WebSocketFrame.ReadULongExactlyAsync(fromStream, false, smallBuffer, cancellationToken).ConfigureAwait(false);
				const uint maxLength = 2147483648; // 2GB - not part of the spec but just a precaution. Send large volumes of data in smaller frames.

				// protect ourselves against bad data
				if (length > maxLength || length < 0)
					throw new ArgumentOutOfRangeException($"Payload length out of range. Min 0 max 2GB. Actual {length:#,##0} bytes.");
			}

			return length;
		}

		/// <summary>
		/// Writes a WebSocket frame into a stream
		/// </summary>
		/// <param name="opCode">The web socket opcode</param>
		/// <param name="fromPayload">Array segment to get payload data from</param>
		/// <param name="toStream">Stream to write to</param>
		/// <param name="isLastFrame">True is this is the last frame in this message (usually true)</param>
		public static void Write(WebSocketOpCode opCode, ArraySegment<byte> fromPayload, MemoryStream toStream, bool isLastFrame, bool isClient)
		{
			var memoryStream = toStream;
			var finBitSetAsByte = isLastFrame ? (byte)0x80 : (byte)0x00;
			var byte1 = (byte)(finBitSetAsByte | (byte)opCode);
			memoryStream.WriteByte(byte1);

			// NB, set the mask flag if we are constructing a client frame
			var maskBitSetAsByte = isClient ? (byte)0x80 : (byte)0x00;

			// depending on the size of the length we want to write it as a byte, ushort or ulong
			if (fromPayload.Count < 126)
			{
				var byte2 = (byte)(maskBitSetAsByte | (byte)fromPayload.Count);
				memoryStream.WriteByte(byte2);
			}
			else if (fromPayload.Count <= ushort.MaxValue)
			{
				var byte2 = (byte)(maskBitSetAsByte | 126);
				memoryStream.WriteByte(byte2);
				WebSocketFrame.WriteUShort((ushort)fromPayload.Count, memoryStream, false);
			}
			else
			{
				var byte2 = (byte)(maskBitSetAsByte | 127);
				memoryStream.WriteByte(byte2);
				WebSocketFrame.WriteULong((ulong)fromPayload.Count, memoryStream, false);
			}

			// if we are creating a client frame then we MUST mack the payload as per the spec
			if (isClient)
			{
				var maskKey = CryptoService.GenerateRandomKey(WebSocketFrame.MaskKeyLength);
				memoryStream.Write(maskKey, 0, maskKey.Length);

				// mask the payload
				var maskKeyArraySegment = new ArraySegment<byte>(maskKey, 0, maskKey.Length);
				WebSocketFrame.ToggleMask(maskKeyArraySegment, fromPayload);
			}

			memoryStream.Write(fromPayload.Array, fromPayload.Offset, fromPayload.Count);
		}

		public static async Task ReadExactlyAsync(int length, Stream stream, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			if (length == 0)
				return;

			if (buffer.Count < length)
			{
				// TODO: it is not impossible to get rid of this, just a little tricky
				// if the supplied buffer is too small for the payload then we should only return the number of bytes in the buffer
				// this will have to propogate all the way up the chain
				throw new InternalBufferOverflowException($"Unable to read {length} bytes into buffer (offset: {buffer.Offset} size: {buffer.Count}). Use a larger read buffer");
			}

			var offset = 0;
			do
			{
				var read = await stream.ReadAsync(buffer.Array, buffer.Offset + offset, length - offset, cancellationToken).ConfigureAwait(false);
				if (read == 0)
					throw new EndOfStreamException($"Unexpected end of stream encountered whilst attempting to read {length:#,##0} bytes");
				offset += read;
			}
			while (offset < length);
		}

		public static async Task<ushort> ReadUShortExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			await WebSocketFrame.ReadExactlyAsync(2, stream, buffer, cancellationToken).ConfigureAwait(false);

			if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 2);

			return BitConverter.ToUInt16(buffer.Array, buffer.Offset);
		}

		public static async Task<ulong> ReadULongExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			await WebSocketFrame.ReadExactlyAsync(8, stream, buffer, cancellationToken).ConfigureAwait(false);

			if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 8);

			return BitConverter.ToUInt64(buffer.Array, buffer.Offset);
		}

		public static async Task<long> ReadLongExactlyAsync(Stream stream, bool isLittleEndian, ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			await WebSocketFrame.ReadExactlyAsync(8, stream, buffer, cancellationToken).ConfigureAwait(false);

			if (!isLittleEndian)
				Array.Reverse(buffer.Array, buffer.Offset, 8);

			return BitConverter.ToInt64(buffer.Array, buffer.Offset);
		}

		public static void WriteInt(int value, Stream stream, bool isLittleEndian)
		{
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
		}

		public static void WriteULong(ulong value, Stream stream, bool isLittleEndian)
		{
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
		}

		public static void WriteLong(long value, Stream stream, bool isLittleEndian)
		{
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
		}

		public static void WriteUShort(ushort value, Stream stream, bool isLittleEndian)
		{
			var buffer = value.ToBytes();
			if (BitConverter.IsLittleEndian && !isLittleEndian)
				Array.Reverse(buffer);

			stream.Write(buffer, 0, buffer.Length);
		}
	}
}