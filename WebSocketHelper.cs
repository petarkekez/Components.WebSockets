﻿#region Related components
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets.Exceptions;
#endregion

namespace net.vieapps.Components.WebSockets
{
	internal static class WebSocketHelper
	{
		static int _BufferLength = 16 * 1024;

		/// <summary>
		/// Gets the length of receiving buffer
		/// </summary>
		public static int BufferLength { get { return WebSocketHelper._BufferLength; } }

		/// <summary>
		/// Sets the length of receiving buffer
		/// </summary>
		/// <param name="length"></param>
		public static void SetBufferLength(int length = 16384)
		{
			if (length >= 1024)
				WebSocketHelper._BufferLength = length;
		}

		/// <summary>
		/// Gets a factory to get recyclable memory stream with RecyclableMemoryStreamManager class to limit LOH fragmentation and improve performance
		/// </summary>
		/// <returns></returns>
		public static Func<MemoryStream> GetRecyclableMemoryStreamFactory()
		{
			return new Microsoft.IO.RecyclableMemoryStreamManager(16 * 1024, 4, 128 * 1024).GetStream;
		}

		/// <summary>
		/// Reads the HTTP header
		/// </summary>
		/// <param name="stream">The stream to read from</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>The HTTP header</returns>
		public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var buffer = new byte[WebSocketHelper.BufferLength];
			var offset = 0;
			var read = 0;

			do
			{
				if (offset >= WebSocketHelper.BufferLength)
					throw new EntityTooLargeException("HTTP header message too large to fit in buffer");

				read = await stream.ReadAsync(buffer, offset, WebSocketHelper.BufferLength - offset, cancellationToken).ConfigureAwait(false);
				offset += read;
				var header = buffer.GetString(offset);

				// as per HTTP specification, all headers should end like this
				if (header.Contains("\r\n\r\n"))
					return header;
			}
			while (read > 0);

			return string.Empty;
		}

		/// <summary>
		/// Writes the HTTP header
		/// </summary>
		/// <param name="header">The header (without the new line characters)</param>
		/// <param name="stream">The stream to write to</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task WriteHttpHeaderAsync(string header, Stream stream, CancellationToken cancellationToken = default(CancellationToken))
		{
			var bytes = (header.Trim() + "\r\n\r\n").ToBytes();
			await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Computes a WebSocket accept key from a given key
		/// </summary>
		/// <param name="key">The WebSocket request key</param>
		/// <returns>A WebSocket accept key</returns>
		public static string ComputeAcceptKey(string key)
		{
			return (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").GetSHA1(true);
		}

		/// <summary>
		/// Negotiates sub-protocol
		/// </summary>
		/// <param name="server"></param>
		/// <param name="client"></param>
		/// <returns></returns>
		public static string NegotiateSubProtocol(IEnumerable<string> server, IEnumerable<string> client)
		{
			if (!server.Any() || !client.Any())
				return null;
			var matches = client.Intersect(server);
			return matches.Any()
				? matches.First()
				: throw new SubProtocolNegotiationFailedException("Unable to negotiate a subprotocol");
		}
	}
}