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
		const uint FILE_SPLIT = 0x1;
		const uint FILE_OBFUSCATED = 0x2; 
		const uint FILE_COMPRESSED = 0x4;
		const uint FILE_INVALID = 0x8; 

		const uint LINK_PREV = 0x1;
		const uint LINK_NEXT = 0x2;
		struct VolumeHeader {
			public uint dataOffset;
			public uint dataOffsetHigh;
			
			public uint firstFileIndex;
			public uint lastFileIndex;
			public uint firstFileOffset;
			public uint firstFileOffsetHigh;
			public uint firstFileSizeExpanded;
			public uint firstFileSizeExpandedHigh;
			public uint firstFileSizeCompressed;
			public uint firstFileSizeCompressedHigh;

			public uint lastFileOffset;
			public uint lastFileOffsetHigh;
			public uint lastFileSizeExpanded;
			public uint lastFileSizeExpandedHigh;
			public uint lastFileSizeCompressed;
			public uint lastFileSizeCompressedHigh;
			public VolumeHeader(BinaryReader reader){
				dataOffset = reader.ReadUInt32();
				dataOffsetHigh = reader.ReadUInt32();

				firstFileIndex = reader.ReadUInt32();
				lastFileIndex = reader.ReadUInt32();
				firstFileOffset = reader.ReadUInt32();
				firstFileOffsetHigh = reader.ReadUInt32();
				firstFileSizeExpanded = reader.ReadUInt32();
				firstFileSizeExpandedHigh = reader.ReadUInt32();
				firstFileSizeCompressed = reader.ReadUInt32();
				firstFileSizeCompressedHigh = reader.ReadUInt32();

				lastFileOffset = reader.ReadUInt32();
				lastFileOffsetHigh = reader.ReadUInt32();
				lastFileSizeExpanded = reader.ReadUInt32();
				lastFileSizeExpandedHigh = reader.ReadUInt32();
				lastFileSizeCompressed = reader.ReadUInt32();
				lastFileSizeCompressedHigh = reader.ReadUInt32();

			}
		}

		struct CommonHeader {
			readonly public uint version;
			readonly public uint volumeInfo;
			readonly public long cabDescriptorOffset;
			readonly public uint cabDescriptorSize;
			public CommonHeader(BinaryReader reader) {
					version = reader.ReadUInt32(); 
					volumeInfo = reader.ReadUInt32(); 
					cabDescriptorOffset = (long)reader.ReadUInt32(); 
					cabDescriptorSize = reader.ReadUInt32();

			}
		}
		struct CabDescriptor {
			readonly public long fileTableOffset;
			public readonly uint fileTableSize;
			public readonly uint fileTableSize2;
			public readonly uint directoryCount;
			public readonly uint fileCount;
			readonly public long fileTableOffset2;
			public CabDescriptor(BinaryReader reader, CommonHeader commonHeader){
				reader.BaseStream.Seek(commonHeader.cabDescriptorOffset + 0xC,SeekOrigin.Begin);
				fileTableOffset = (long)reader.ReadUInt32();
				reader.ReadUInt32();
				fileTableSize = reader.ReadUInt32();
				fileTableSize2 = reader.ReadUInt32();
				directoryCount = reader.ReadUInt32();
				reader.ReadUInt64();
				fileCount = reader.ReadUInt32();
				fileTableOffset2 = (long)reader.ReadUInt32();
				
			}
		}
		struct FileDescriptor {
			public readonly ushort	flags;
			public readonly uint	expandedSize;
			public readonly uint	compressedSize;
			public readonly uint	dataOffset;
			public readonly byte[]	md5; 
			public readonly uint 	nameOffset;
			public readonly ushort	directoryIndex;
			public readonly uint	linkPrevious;
			public readonly uint	linkNext;
			public readonly byte 	linkFlags;
			public readonly ushort	volume;
			public readonly string	filename;

			public FileDescriptor(BinaryReader reader, long tableOffset ) {
				flags 			= reader.ReadUInt16();
				expandedSize 		= reader.ReadUInt32();
				reader.ReadUInt32();
				compressedSize 		= reader.ReadUInt32();
				reader.ReadUInt32();
				dataOffset 		= reader.ReadUInt32();
				reader.ReadUInt32();
				md5 			= reader.ReadBytes(0x10);
				reader.ReadBytes(0x10);
				nameOffset 		= reader.ReadUInt32();
				directoryIndex 		= reader.ReadUInt16();
				reader.ReadBytes(0xc);
				linkPrevious 		= reader.ReadUInt32();
				linkNext 		= reader.ReadUInt32();
				linkFlags		= reader.ReadByte();
				volume			= reader.ReadUInt16();
				var pos_save		= reader.BaseStream.Position;

				reader.BaseStream.Seek(tableOffset + nameOffset, SeekOrigin.Begin);
				var sb = new System.Text.StringBuilder();
				byte c = reader.ReadByte();
				while(c != 0) {
					sb.Append((char)c);
					c = reader.ReadByte();
				}
				filename = sb.ToString();
				reader.BaseStream.Seek(pos_save, SeekOrigin.Begin);
			}

		}
		readonly Stream s;
		CommonHeader commonHeader;
		CabDescriptor cabDescriptor;
		List<uint> directoryTable;
		List<uint> fileTable;
		string commonPath;
		Dictionary<uint,string> directoryNames;
		Dictionary<uint,FileDescriptor> fileDescriptors;

		public InstallShieldCABExtractor(string filename) {
			s = GlobalFileSystem.Open(filename);
			var buff = new List<char>(filename.Substring(0,filename.LastIndexOf('.')).ToCharArray());
			for(int i = buff.Count-1; char.IsNumber(buff[i]); --i) {
				buff.RemoveAt(i);
			}
			commonPath = new string(buff.ToArray());

			var reader = new BinaryReader(s);
			var signature = reader.ReadUInt32();
			if( signature != 0x28635349) throw new InvalidDataException("Not an Installshield CAB package");
			commonHeader = new CommonHeader(reader);
			cabDescriptor = new CabDescriptor(reader,commonHeader);
			reader.BaseStream.Seek(commonHeader.cabDescriptorOffset + cabDescriptor.fileTableOffset, SeekOrigin.Begin);

			directoryTable = new List<uint>();
			for(uint i = cabDescriptor.directoryCount; i > 0; --i) {
				directoryTable.Add(reader.ReadUInt32());
			}

			fileTable = new List<uint>();
			for(uint i = cabDescriptor.fileCount; i > 0; --i) {
				fileTable.Add(reader.ReadUInt32());
			}
			directoryNames  = new Dictionary<uint,string>();
			fileDescriptors = new Dictionary<uint,FileDescriptor>();

		}
		public string DirectoryName(uint index){
			if(directoryNames.ContainsKey(index))
				return directoryNames[index];
			var reader = new BinaryReader(s);
			reader.BaseStream.Seek(commonHeader.cabDescriptorOffset + 
					cabDescriptor.fileTableOffset +
					directoryTable[(int)index]
					,SeekOrigin.Begin);
			var sb = new System.Text.StringBuilder();
			byte c = reader.ReadByte();
			while(c != 0) {
				sb.Append((char)c);
				c = reader.ReadByte();
			}
			return sb.ToString();
		}
		public uint DirectoryCount() {
			return cabDescriptor.directoryCount;
		}
		public string FileName(uint index){
			if(!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);		
			return fileDescriptors[index].filename;
		}
		void AddFileDescriptorToList(uint index) {
			var reader = new BinaryReader(s);
			reader.BaseStream.Seek(commonHeader.cabDescriptorOffset +
					cabDescriptor.fileTableOffset +
					cabDescriptor.fileTableOffset2 +
					index*0x57,
					SeekOrigin.Begin);
			fileDescriptors.Add(index,new FileDescriptor(reader,
					commonHeader.cabDescriptorOffset +
					cabDescriptor.fileTableOffset)
					);
		}
		public uint FileCount(){
			return cabDescriptor.fileCount;
		}
		public void ExtractFile(uint index, string fileName){
			if(!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);		
			var fd = fileDescriptors[index];
			if((fd.flags & FILE_INVALID) != 0) throw new Exception("File Invalid");	
			if((fd.linkFlags & LINK_PREV) != 0){
				ExtractFile(fd.linkPrevious,fileName);
				return;
			}
			if((fd.flags&FILE_SPLIT) != 0 || (fd.flags&FILE_OBFUSCATED) != 0) 
				throw new Exception("Haven't implemented");
			
			var fil = GlobalFileSystem.Open(string.Format("{0}{1}.cab",commonPath,fd.volume));
			var reader = new BinaryReader(fil);
			if(reader.ReadUInt32() != 0x28635349) throw new InvalidDataException("Not an Installshield CAB package");
			new CommonHeader(reader);
			new VolumeHeader(reader);
			reader.BaseStream.Seek(fd.dataOffset,SeekOrigin.Begin);
			var writer = new BinaryWriter(File.Open(fileName,FileMode.Create));
			if((fd.flags&FILE_COMPRESSED) != 0) {
				uint bytes_to_read = fd.compressedSize;	
				ushort bytes_to_extract;
				byte[] read_buffer;
				byte[] write_buffer;
				while(bytes_to_read > 0) {
					long p = reader.BaseStream.Position;
					bytes_to_extract = reader.ReadUInt16();
					read_buffer = reader.ReadBytes(bytes_to_extract);
					write_buffer = Ionic.Zlib.DeflateStream.UncompressBuffer(read_buffer);
					writer.Write(write_buffer);
					bytes_to_read -= (uint)bytes_to_extract + 2;
				}
				writer.Dispose();
			} else {
				writer.Write(reader.ReadBytes((int)fd.expandedSize));
				writer.Dispose();
			}

		}
		public void Dispose(){
			s.Dispose();
		}
	}
}
