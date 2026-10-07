using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Install_Versions
{
    public class MsiXvdStream : IDisposable
    {
        private FileStream _fs;
        private BinaryReader _br;

        public ulong DriveSize;
        public uint UserDataLength;
        public uint XvcDataLength;
        public uint BlockSize;
        public uint DynamicHeaderLength;
        public uint EmbeddedXvdLength;
        public byte[] VdUid = new byte[16];
        public bool IsEncrypted;
        public bool DataIntegrity;
        public bool Resiliency;

        private ulong _xvdUserDataOffset;
        private ulong _hashTreePageOffset;
        private ulong _hashTreePageCount;
        private ulong _hashTreeLevels;
        private ulong _mutableDataOffset;
        private int _hashEntryLength;
        private ulong _totalHashedPages;
        private int _mutableDataPageCount;

        private List<UserPackageFile> _packageFiles = new List<UserPackageFile>();

        private uint _regionCount;
        private uint _updateSegmentCount;

        public List<XvcRegion> Regions = new List<XvcRegion>();
        public List<XvcSegment> Segments = new List<XvcSegment>();
        public List<XvcUpdateSegment> UpdateSegments = new List<XvcUpdateSegment>();

        private class UserPackageFile
        {
            public string Path;
            public byte[] Data;
        }

        public class XvcRegion
        {
            public uint Id;
            public ushort KeyId;
            public uint FirstSegmentIndex;
            public ulong Offset;
            public ulong Length;
        }

        public class XvcSegment
        {
            public uint PageNum;
            public ulong FileSize;
            public string Path;
        }

        public class XvcUpdateSegment
        {
            public uint PageNum;
            public ulong Hash;
        }

        public MsiXvdStream(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _br = new BinaryReader(_fs);
        }

        public void Parse()
        {
            ParseHeader();

            const ulong XVD_HEADER_SIZE = 12288;
            ulong embeddedXvdPageCount = BytesToPages(EmbeddedXvdLength);
            _mutableDataOffset = PageToOffset(embeddedXvdPageCount) + XVD_HEADER_SIZE;

            ulong userDataPageCount = BytesToPages(UserDataLength);
            ulong xvcDataPageCount = BytesToPages(XvcDataLength);
            ulong dynamicHeaderPageCount = BytesToPages(DynamicHeaderLength);
            ulong drivePageCount = BytesToPages(DriveSize);

            _totalHashedPages = drivePageCount + userDataPageCount + xvcDataPageCount + dynamicHeaderPageCount;
            _hashTreePageCount = CalculateHashPageCount(_totalHashedPages, out _hashTreeLevels, Resiliency);

            ulong mutableDataLength = PageToOffset((ulong)_mutableDataPageCount);
            _hashTreePageOffset = mutableDataLength + _mutableDataOffset;

            ulong hashTreeBytes = DataIntegrity ? PageToOffset(_hashTreePageCount) : 0;
            _xvdUserDataOffset = hashTreeBytes + _hashTreePageOffset;

            Console.WriteLine("[DEBUG] MutableDataPageCount = " + _mutableDataPageCount);
            Console.WriteLine("[DEBUG] IsEncrypted = " + IsEncrypted);
            Console.WriteLine("[DEBUG] DataIntegrity = " + DataIntegrity);
            Console.WriteLine("[DEBUG] UserDataLength = " + UserDataLength);
            Console.WriteLine("[DEBUG] DriveSize = " + DriveSize);
            Console.WriteLine("[DEBUG] _hashTreePageCount = " + _hashTreePageCount);
            Console.WriteLine("[DEBUG] _hashTreeLevels = " + _hashTreeLevels);
            Console.WriteLine("[DEBUG] _xvdUserDataOffset = " + _xvdUserDataOffset);

            ParseUserData();

            var segMeta = GetPackageFile("SegmentMetadata.bin");
            Console.WriteLine("[DEBUG] SegmentMetadata.bin found: " + (segMeta != null));
            if (segMeta != null)
            {
                ParseSegments(segMeta);
                Console.WriteLine("[DEBUG] Segments.Count = " + Segments.Count);
            }
            else
            {
                Console.WriteLine("[DEBUG] _packageFiles.Count = " + _packageFiles.Count);
                foreach (var f in _packageFiles)
                    Console.WriteLine("[DEBUG]   package file: " + f.Path);
            }

            ParseXvcArea();
            Console.WriteLine("[DEBUG] Regions.Count = " + Regions.Count);
            foreach (var r in Regions)
            {
                Console.WriteLine("[DEBUG]   region: Id=0x" + r.Id.ToString("X8")
                    + " FirstSeg=" + r.FirstSegmentIndex
                    + " Offset=" + r.Offset
                    + " Length=" + r.Length);
            }
            Console.WriteLine("[DEBUG] UpdateSegments.Count = " + UpdateSegments.Count);
        }

        private void ParseHeader()
        {
            _fs.Position = 0;
            byte[] header = _br.ReadBytes(12288);

            int pos = 512 + 8;

            uint volumes = BitConverter.ToUInt32(header, pos); pos += 4;
            IsEncrypted = (volumes & 2) == 0;
            DataIntegrity = (volumes & 4) == 0;
            Resiliency = (volumes & 0x10) != 0;

            pos += 4;
            pos += 8;
            DriveSize = BitConverter.ToUInt64(header, pos); pos += 8;

            Buffer.BlockCopy(header, pos, VdUid, 0, 16);
            pos += 16;
            pos += 16;
            pos += 32;
            pos += 32;
            pos += 4;
            pos += 4;

            EmbeddedXvdLength = BitConverter.ToUInt32(header, pos); pos += 4;
            UserDataLength = BitConverter.ToUInt32(header, pos); pos += 4;
            XvcDataLength = BitConverter.ToUInt32(header, pos); pos += 4;
            DynamicHeaderLength = BitConverter.ToUInt32(header, pos); pos += 4;
            BlockSize = BitConverter.ToUInt32(header, pos); pos += 4;

            _mutableDataPageCount = header[0x470];
            _hashEntryLength = IsEncrypted ? 20 : 24;
        }

        private void ParseUserData()
        {
            _fs.Position = (long)_xvdUserDataOffset;
            byte[] userData = _br.ReadBytes((int)UserDataLength);

            uint userHeaderLength = BitConverter.ToUInt32(userData, 0);
            uint userType = BitConverter.ToUInt32(userData, 8);

            if (userType != 0) return;

            int filesHeaderOffset = (int)userHeaderLength;
            int fileCount = BitConverter.ToInt32(userData, filesHeaderOffset + 4 + 260 * 2);

            Console.WriteLine("[USERDATA] fileCount = " + fileCount);

            int entrySize = 260 * 2 + 4 + 4;
            int entriesOffset = filesHeaderOffset + 4 + 260 * 2 + 4;

            for (int i = 0; i < fileCount; i++)
            {
                int entryOffset = entriesOffset + i * entrySize;
                if (entryOffset + entrySize > userData.Length) break;

                string path = ReadWString(userData, entryOffset, 260);
                uint size = BitConverter.ToUInt32(userData, entryOffset + 260 * 2);
                uint offset = BitConverter.ToUInt32(userData, entryOffset + 260 * 2 + 4);

                int dataOffset = (int)(userHeaderLength + offset);
                if (dataOffset + size > userData.Length) continue;

                byte[] data = new byte[size];
                Buffer.BlockCopy(userData, dataOffset, data, 0, (int)size);

                Console.WriteLine("[USERDATA]   file[" + i + "] '" + path + "' (" + size + " bytes)");
                _packageFiles.Add(new UserPackageFile { Path = path, Data = data });
            }
        }

        private void ParseSegments(byte[] segMetaData)
        {
            uint segmentCount = BitConverter.ToUInt32(segMetaData, 16);
            uint headerLength = BitConverter.ToUInt32(segMetaData, 12);

            Console.WriteLine("[SEGMENTS] segmentCount = " + segmentCount);

            int entriesOffset = 100;
            uint pathsBaseOffset = headerLength + segmentCount * 16;

            for (int i = 0; i < segmentCount; i++)
            {
                int entryOffset = entriesOffset + i * 16;
                if (entryOffset + 16 > segMetaData.Length) break;

                ushort pathLength = BitConverter.ToUInt16(segMetaData, entryOffset + 2);
                uint pathOffset = BitConverter.ToUInt32(segMetaData, entryOffset + 4);
                ulong fileSize = BitConverter.ToUInt64(segMetaData, entryOffset + 8);

                int strOffset = (int)(pathsBaseOffset + pathOffset);
                if (strOffset + pathLength * 2 > segMetaData.Length) continue;

                string path = Encoding.Unicode.GetString(segMetaData, strOffset, pathLength * 2);
                Segments.Add(new XvcSegment { FileSize = fileSize, Path = path });
            }
        }

        private void ParseXvcArea()
        {
            ulong userDataPageCount = BytesToPages(UserDataLength);
            ulong xvcOffset = _xvdUserDataOffset + PageToOffset(userDataPageCount);

            _fs.Position = (long)xvcOffset;
            byte[] xvcData = _br.ReadBytes((int)XvcDataLength);

            _regionCount = BitConverter.ToUInt32(xvcData, 16 + 192 * 16 + 256 + 4);
            _updateSegmentCount = BitConverter.ToUInt32(xvcData, 16 + 192 * 16 + 256 + 4 + 4 + 4 + 2 + 2 + 4 + 4 + 8 + 8);

            Console.WriteLine("[XVC] _regionCount = " + _regionCount);
            Console.WriteLine("[XVC] _updateSegmentCount = " + _updateSegmentCount);

            int xvcInfoSize = 3496;
            int regionHeaderSize = 128;
            int regionsOffset = xvcInfoSize;

            for (int i = 0; i < _regionCount; i++)
            {
                int o = regionsOffset + i * regionHeaderSize;
                if (o + regionHeaderSize > xvcData.Length) break;

                Regions.Add(new XvcRegion
                {
                    Id = BitConverter.ToUInt32(xvcData, o),
                    KeyId = BitConverter.ToUInt16(xvcData, o + 4),
                    FirstSegmentIndex = BitConverter.ToUInt32(xvcData, o + 12),
                    Offset = BitConverter.ToUInt64(xvcData, o + 80),
                    Length = BitConverter.ToUInt64(xvcData, o + 88)
                });
            }

            int updateSegmentsOffset = regionsOffset + (int)_regionCount * regionHeaderSize;
            int maxSeg = (xvcData.Length - updateSegmentsOffset) / 12;
            int segCount = Math.Min((int)_updateSegmentCount, maxSeg);

            for (int i = 0; i < segCount; i++)
            {
                int o = updateSegmentsOffset + i * 12;
                UpdateSegments.Add(new XvcUpdateSegment
                {
                    PageNum = BitConverter.ToUInt32(xvcData, o),
                    Hash = BitConverter.ToUInt64(xvcData, o + 4)
                });
            }
        }

        public void ExtractAll(string outputDir, CikKey cik)
        {
            using (var xts = new AesXts(cik.DKey, cik.TKey))
            {
                foreach (var region in Regions)
                {
                    // 跳过 metadata 区域（FirstSegmentIndex = 0）
                    if (region.FirstSegmentIndex == 0)
                        continue;

                    ExtractRegion(region, outputDir, xts);
                }
            }
        }

        private void ExtractRegion(XvcRegion region, string outputDir, AesXts xts)
        {
            if (region.FirstSegmentIndex >= Segments.Count)
            {
                Console.WriteLine("[EXTRACT] 跳过无效 region: 0x" + region.Id.ToString("X8")
                    + " FirstSeg=" + region.FirstSegmentIndex);
                return;
            }

            bool shouldDecrypt = IsEncrypted && region.KeyId != 0xFFFF;

            byte[] iv = new byte[16];
            ulong dataBlockIndex = GetPageOffset(region.Offset - _xvdUserDataOffset);

            Console.WriteLine("[EXTRACT] Region 0x" + region.Id.ToString("X8")
                + " FirstSeg=" + region.FirstSegmentIndex
                + " Offset=" + region.Offset
                + " Length=" + region.Length
                + " dataBlockIndex=" + dataBlockIndex
                + " decrypt=" + shouldDecrypt);

            byte[] hashBlockBuffer = null;
            int hashBlockBufferPos = 0;
            ulong hashBlockPage = 0;
            ulong entryIndex = 0;

            if (shouldDecrypt)
            {
                hashBlockPage = ComputeHashBlockIndexForDataBlock(dataBlockIndex, out entryIndex);
                hashBlockBuffer = ReadHashBlock(hashBlockPage);
                hashBlockBufferPos = (int)(entryIndex * 24);

                Buffer.BlockCopy(BitConverter.GetBytes(region.Id), 0, iv, 4, 4);
                Buffer.BlockCopy(VdUid, 0, iv, 8, 8);

                uint seed = ReadSeedFromHashBlock(hashBlockBuffer, hashBlockBufferPos);
                Buffer.BlockCopy(BitConverter.GetBytes(seed), 0, iv, 0, 4);
            }

            int segIndex = (int)region.FirstSegmentIndex;
            ulong remaining = region.Length;
            ulong pos = region.Offset;

            while (segIndex < Segments.Count && remaining > 0)
            {
                var seg = Segments[segIndex];
                string outPath = Path.Combine(outputDir, seg.Path.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));

                Console.WriteLine("[EXTRACT]   " + seg.Path + " (" + seg.FileSize + " bytes)");

                using (var outFs = new FileStream(outPath, FileMode.Create))
                {
                    ulong fileRemaining = seg.FileSize;
                    while (fileRemaining > 0)
                    {
                        int chunk = (int)Math.Min(4096, fileRemaining);
                        byte[] buffer = new byte[4096];

                        _fs.Position = (long)pos;
                        int read = _fs.Read(buffer, 0, 4096);
                        if (read == 0) break;

                        if (shouldDecrypt && hashBlockBuffer != null)
                        {
                            byte[] decrypted = new byte[4096];
                            xts.Decrypt(buffer, 0, decrypted, 0, 4096, iv);
                            Buffer.BlockCopy(decrypted, 0, buffer, 0, 4096);
                        }

                        outFs.Write(buffer, 0, chunk);

                        pos += 4096;
                        fileRemaining -= (ulong)chunk;
                        remaining -= (ulong)chunk;

                        // 推进 hash tree 位置
                        if (shouldDecrypt)
                        {
                            dataBlockIndex++;
                            hashBlockPage = ComputeHashBlockIndexForDataBlock(dataBlockIndex, out entryIndex);

                            // entryIndex 是 dataBlockIndex % 170
                            if (entryIndex == 0)
                            {
                                hashBlockBuffer = ReadHashBlock(hashBlockPage);
                            }
                            hashBlockBufferPos = (int)(entryIndex * 24);

                            uint seed = ReadSeedFromHashBlock(hashBlockBuffer, hashBlockBufferPos);
                            Buffer.BlockCopy(BitConverter.GetBytes(seed), 0, iv, 0, 4);
                        }
                    }
                }

                segIndex++;
            }
        }

        private byte[] ReadHashBlock(ulong hashBlockPage)
        {
            _fs.Position = (long)(_hashTreePageOffset + PageToOffset(hashBlockPage));
            return _br.ReadBytes(4096);
        }

        private uint ReadSeedFromHashBlock(byte[] hashBlock, int pos)
        {
            if (hashBlock == null) return 0;
            int seedOffset = pos + _hashEntryLength;
            if (seedOffset + 4 > hashBlock.Length) return 0;
            return BitConverter.ToUInt32(hashBlock, seedOffset);
        }

        private ulong ComputeHashBlockIndexForDataBlock(ulong dataBlockIndex, out ulong entryIndex)
        {
            ulong totalHashedPages = _totalHashedPages;
            ulong hashTreeDepth = _hashTreeLevels;
            ulong currentHashLevel = 0;

            entryIndex = dataBlockIndex % 170;

            if (currentHashLevel == 3)
                return 0;

            ulong result = dataBlockIndex / ComputeLevelMultiplier(currentHashLevel + 1);
            hashTreeDepth -= currentHashLevel + 1;

            if (currentHashLevel == 0 && hashTreeDepth != 0)
            {
                result += (totalHashedPages + ComputeLevelMultiplier(2) - 1) / ComputeLevelMultiplier(2);
                hashTreeDepth--;
            }
            if ((currentHashLevel == 0 || currentHashLevel == 1) && hashTreeDepth != 0)
            {
                result += (totalHashedPages + ComputeLevelMultiplier(3) - 1) / ComputeLevelMultiplier(3);
                hashTreeDepth--;
            }
            if (hashTreeDepth != 0)
            {
                result += (totalHashedPages + ComputeLevelMultiplier(4) - 1) / ComputeLevelMultiplier(4);
            }

            return result;
        }

        private static ulong ComputeLevelMultiplier(ulong level)
        {
            ulong result = 1;
            for (ulong i = 0; i < level; i++) result *= 170;
            return result;
        }

        private static ulong CalculateHashPageCount(ulong hashedPages, out ulong hashTreeLevels, bool resilient)
        {
            ulong total = (hashedPages + 170 - 1) / 170;
            hashTreeLevels = 1;

            if (total > 1)
            {
                ulong current = total;
                ulong multiplier = 170;

                while (current > 1)
                {
                    ulong levelPages = (hashedPages + multiplier * 170 - 1) / (multiplier * 170);
                    if (levelPages == 0) break;

                    total += levelPages;
                    multiplier *= 170;
                    current = levelPages;
                    hashTreeLevels++;

                    if (hashTreeLevels >= 5) break;
                }
            }

            if (resilient) total *= 2;
            return total;
        }

        public byte[] GetPackageFile(string path)
        {
            foreach (var f in _packageFiles)
                if (string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                    return f.Data;
            return null;
        }

        private static ulong GetPageOffset(ulong value) { return value / 4096; }
        private static ulong PageToOffset(ulong value) { return value * 4096; }
        private static ulong BytesToPages(ulong bytes) { return (bytes + 4096 - 1) / 4096; }

        private static string ReadWString(byte[] data, int offset, int maxChars)
        {
            int end = offset;
            int limit = Math.Min(offset + maxChars * 2, data.Length);
            while (end + 1 < limit)
            {
                if (data[end] == 0 && data[end + 1] == 0) break;
                end += 2;
            }
            if (end <= offset) return "";
            return Encoding.Unicode.GetString(data, offset, end - offset);
        }

        public void Dispose()
        {
            if (_br != null) _br.Dispose();
            if (_fs != null) _fs.Dispose();
        }
    }
}