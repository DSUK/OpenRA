#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using OpenRA.FileFormats;
namespace OpenRA.FileSystem
{
	public class InstallShieldCABExtractor : IDisposable
	{
		const uint FILESPLIT = 0x1;
		const uint FILEOBFUSCATED = 0x2;
		const uint FILECOMPRESSED = 0x4;
		const uint FILEINVALID = 0x8;

		const uint LINKPREV = 0x1;
		const uint LINKNEXT = 0x2;
		struct VolumeHeader {
			public uint DataOffset;
			public uint DataOffsetHigh;
			public uint FirstFileIndex;
			public uint LastFileIndex;
			public uint FirstFileOffset;
			public uint FirstFileOffsetHigh;
			public uint FirstFileSizeExpanded;
			public uint FirstFileSizeExpandedHigh;
			public uint FirstFileSizeCompressed;
			public uint FirstFileSizeCompressedHigh;

			public uint LastFileOffset;
			public uint LastFileOffsetHigh;
			public uint LastFileSizeExpanded;
			public uint LastFileSizeExpandedHigh;
			public uint LastFileSizeCompressed;
			public uint LastFileSizeCompressedHigh;
			public VolumeHeader(BinaryReader reader)
			{
				DataOffset = reader.ReadUInt32();
				DataOffsetHigh = reader.ReadUInt32();

				FirstFileIndex = reader.ReadUInt32();
				LastFileIndex = reader.ReadUInt32();
				FirstFileOffset = reader.ReadUInt32();
				FirstFileOffsetHigh = reader.ReadUInt32();
				FirstFileSizeExpanded = reader.ReadUInt32();
				FirstFileSizeExpandedHigh = reader.ReadUInt32();
				FirstFileSizeCompressed = reader.ReadUInt32();
				FirstFileSizeCompressedHigh = reader.ReadUInt32();

				LastFileOffset = reader.ReadUInt32();
				LastFileOffsetHigh = reader.ReadUInt32();
				LastFileSizeExpanded = reader.ReadUInt32();
				LastFileSizeExpandedHigh = reader.ReadUInt32();
				LastFileSizeCompressed = reader.ReadUInt32();
				LastFileSizeCompressedHigh = reader.ReadUInt32();
			}
		}

		struct CommonHeader
		{
			public readonly uint Version;
			public readonly uint VolumeInfo;
			public readonly long CabDescriptorOffset;
			public readonly uint CabDescriptorSize;
			public CommonHeader(BinaryReader reader)
			{
					Version = reader.ReadUInt32();
					VolumeInfo = reader.ReadUInt32();
					CabDescriptorOffset = (long)reader.ReadUInt32();
					CabDescriptorSize = reader.ReadUInt32();
			}
		}

		struct CabDescriptor
		{
			public readonly long FileTableOffset;
			public readonly uint FileTableSize;
			public readonly uint FileTableSize2;
			public readonly uint DirectoryCount;
			public readonly uint FileCount;
			public readonly long FileTableOffset2;
			public CabDescriptor(BinaryReader reader, CommonHeader commonHeader)
			{
				reader.BaseStream.Seek(commonHeader.CabDescriptorOffset + 0xC,
						SeekOrigin.Begin);
				FileTableOffset = (long)reader.ReadUInt32();
				reader.ReadUInt32();
				FileTableSize = reader.ReadUInt32();
				FileTableSize2 = reader.ReadUInt32();
				DirectoryCount = reader.ReadUInt32();
				reader.ReadUInt64();
				FileCount = reader.ReadUInt32();
				FileTableOffset2 = (long)reader.ReadUInt32();
			}
		}

		struct FileDescriptor
		{
			public readonly ushort	Flags;
			public readonly uint	ExpandedSize;
			public readonly uint	CompressedSize;
			public readonly uint	DataOffset;
			public readonly byte[]	MD5;
			public readonly uint 	NameOffset;
			public readonly ushort	DirectoryIndex;
			public readonly uint	LinkPrevious;
			public readonly uint	LinkNext;
			public readonly byte 	LinkFlags;
			public readonly ushort	Volume;
			public readonly string	Filename;

			public FileDescriptor(BinaryReader reader, long tableOffset)
			{
				Flags 			= reader.ReadUInt16();
				ExpandedSize 		= reader.ReadUInt32();
				reader.ReadUInt32();
				CompressedSize 		= reader.ReadUInt32();
				reader.ReadUInt32();
				DataOffset 		= reader.ReadUInt32();
				reader.ReadUInt32();
				MD5 			= reader.ReadBytes(0x10);
				reader.ReadBytes(0x10);
				NameOffset 		= reader.ReadUInt32();
				DirectoryIndex 		= reader.ReadUInt16();
				reader.ReadBytes(0xc);
				LinkPrevious 		= reader.ReadUInt32();
				LinkNext 		= reader.ReadUInt32();
				LinkFlags		= reader.ReadByte();
				Volume			= reader.ReadUInt16();
				var pos_save		= reader.BaseStream.Position;

				reader.BaseStream.Seek(tableOffset + NameOffset, SeekOrigin.Begin);
				var sb = new System.Text.StringBuilder();
				byte c = reader.ReadByte();
				while (c != 0) {
					sb.Append((char)c);
					c = reader.ReadByte();
				}

				Filename = sb.ToString();
				reader.BaseStream.Seek(pos_save, SeekOrigin.Begin);
			}
		}

		readonly Stream s;
		CommonHeader commonHeader;
		CabDescriptor cabDescriptor;
		List<uint> directoryTable;
		List<uint> fileTable;
		string commonPath;
		Dictionary<uint, string> directoryNames;
		Dictionary<uint, FileDescriptor> fileDescriptors;

		public InstallShieldCABExtractor(string filename)
		{
			s = GlobalFileSystem.Open(filename);
			var buff = new List<char>(filename.Substring(0, filename.LastIndexOf('.')).ToCharArray());
			for (int i = buff.Count - 1; char.IsNumber(buff[i]); --i) {
				buff.RemoveAt(i);
			}

			commonPath = new string(buff.ToArray());
			var reader = new BinaryReader(s);
			var signature = reader.ReadUInt32();
			if (signature != 0x28635349) throw new InvalidDataException("Not an Installshield CAB package");
			commonHeader = new CommonHeader(reader);
			cabDescriptor = new CabDescriptor(reader, commonHeader);
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset + cabDescriptor.FileTableOffset, SeekOrigin.Begin);

			directoryTable = new List<uint>();
			for (uint i = cabDescriptor.DirectoryCount; i > 0; --i) {
				directoryTable.Add(reader.ReadUInt32());
			}

			fileTable = new List<uint>();
			for (uint i = cabDescriptor.FileCount; i > 0; --i) {
				fileTable.Add(reader.ReadUInt32());
			}

			directoryNames  = new Dictionary<uint, string>();
			fileDescriptors = new Dictionary<uint, FileDescriptor>();
		}

		public string DirectoryName(uint index)
		{
			if (directoryNames.ContainsKey(index))
				return directoryNames[index];
			var reader = new BinaryReader(s);
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset +
					cabDescriptor.FileTableOffset +
					directoryTable[(int)index],
					SeekOrigin.Begin);
			var sb = new System.Text.StringBuilder();
			byte c = reader.ReadByte();
			while (c != 0) {
				sb.Append((char)c);
				c = reader.ReadByte();
			}

			return sb.ToString();
		}

		public uint DirectoryCount() {
			return cabDescriptor.DirectoryCount;
		}

		public string FileName(uint index)
		{
			if (!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);
			return fileDescriptors[index].Filename;
		}

		void AddFileDescriptorToList(uint index) {
			var reader = new BinaryReader(s);
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset +
					cabDescriptor.FileTableOffset +
					cabDescriptor.FileTableOffset2 +
					index * 0x57,
					SeekOrigin.Begin);
			var fd = new FileDescriptor(reader,
				commonHeader.CabDescriptorOffset + cabDescriptor.FileTableOffset);
			fileDescriptors.Add(index, fd);
		}

		public uint FileCount()
		{
			return cabDescriptor.FileCount;
		}

		public void ExtractFile(uint index, string fileName)
		{
			if (!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);
			var fd = fileDescriptors[index];
			if ((fd.Flags & FILEINVALID) != 0) throw new Exception("File Invalid");
			if ((fd.LinkFlags & LINKPREV) != 0)
			{
				ExtractFile(fd.LinkPrevious, fileName);
				return;
			}

			if ((fd.Flags & FILESPLIT) != 0 || (fd.Flags & FILEOBFUSCATED) != 0)
				throw new Exception("Haven't implemented");

			var fil = GlobalFileSystem.Open(string.Format("{0}{1}.cab", commonPath, fd.Volume));
			var reader = new BinaryReader(fil);
			if (reader.ReadUInt32() != 0x28635349)
				throw new InvalidDataException("Not an Installshield CAB package");
			new CommonHeader(reader);
			new VolumeHeader(reader);
			reader.BaseStream.Seek(fd.DataOffset, SeekOrigin.Begin);
			var writer = new BinaryWriter(File.Open(fileName, FileMode.Create));
			if ((fd.Flags & FILECOMPRESSED) != 0) {
				uint bytes_to_read = fd.CompressedSize;
				ushort bytes_to_extract;
				byte[] read_buffer;
				byte[] write_buffer;
				while (bytes_to_read > 0) {
					long p = reader.BaseStream.Position;
					bytes_to_extract = reader.ReadUInt16();
					read_buffer = reader.ReadBytes(bytes_to_extract);
					write_buffer = Ionic.Zlib.DeflateStream.UncompressBuffer(read_buffer);
					writer.Write(write_buffer);
					bytes_to_read -= (uint)bytes_to_extract + 2;
				}

				writer.Dispose();
			} else {
				writer.Write(reader.ReadBytes((int)fd.ExpandedSize));
				writer.Dispose();
			}
		}

		public void Dispose()
		{
			s.Dispose();
		}
	}
}
