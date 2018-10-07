using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BTDB.KVDBLayer.BTree;
using BTDB.StreamLayer;

namespace BTDB.KVDBLayer
{
    class Compactor
    {
        readonly IKeyValueDBInternal _keyValueDB;
        IRootNodeInternal _root;
        FileStat[] _fileStats;
        Dictionary<ulong, uint> _newPositionMap;
        readonly CancellationToken _cancellation;

        struct FileStat
        {
            uint _valueLength;
            uint _totalLength;
            bool _forbidToDelete;

            internal FileStat(uint size)
            {
                _totalLength = size;
                _valueLength = 0;
                _forbidToDelete = false;
            }

            internal void AddLength(uint length)
            {
                _valueLength += length;
            }

            internal uint CalcWasteIgnoreUseless()
            {
                if (_totalLength == 0) return 0;
                if (_valueLength == 0) return 0;
                return _totalLength - _valueLength;
            }

            internal bool Useless()
            {
                return _totalLength != 0 && _valueLength == 0 && !_forbidToDelete;
            }

            internal uint CalcUsed()
            {
                return _valueLength;
            }

            internal void MarkForbidToDelete()
            {
                _forbidToDelete = true;
            }

            internal bool IsFreeToDelete()
            {
                return !_forbidToDelete;
            }
        }

        internal Compactor(IKeyValueDBInternal keyValueDB, CancellationToken cancellation)
        {
            _keyValueDB = keyValueDB;
            _cancellation = cancellation;
        }

        void ForbidDeletePreservingHistory(long dontTouchGeneration, long[] usedFilesFromOldGenerations)
        {
            for (var i = 0; i < _fileStats.Length; i++)
            {
                if (!_keyValueDB.ContainsValuesAndDoesNotTouchGeneration((uint)i, dontTouchGeneration)
                    || (usedFilesFromOldGenerations != null && Array.BinarySearch(usedFilesFromOldGenerations, _keyValueDB.GetGeneration((uint)i)) >= 0))
                    _fileStats[i].MarkForbidToDelete();
            }
        }

        void MarkTotallyUselessFilesAsUnknown()
        {
            List<uint> toRemoveFileIds = null;
            for (var i = 0; i < _fileStats.Length; i++)
            {
                if (_fileStats[i].Useless())
                {
                    if (toRemoveFileIds == null)
                        toRemoveFileIds = new List<uint>();
                    toRemoveFileIds.Add((uint)i);
                }
            }
            if (toRemoveFileIds != null)
                _keyValueDB.MarkAsUnknown(toRemoveFileIds);
        }

        internal bool Run()
        {
            if (_keyValueDB.FileCollection.GetCount() == 0) return false;
            _root = _keyValueDB.OldestRoot;
            var dontTouchGeneration = _keyValueDB.GetGeneration(_keyValueDB.GetTrLogFileId(_root));
            var preserveKeyIndexKey = _keyValueDB.CalculatePreserveKeyIndexKeyFromKeyIndexInfos(_keyValueDB.BuildKeyIndexInfos());
            var preserveKeyIndexGeneration = _keyValueDB.CalculatePreserveKeyIndexGeneration(preserveKeyIndexKey);
            InitFileStats(dontTouchGeneration);
            long[] usedFilesFromOldGenerations = null;
            if (preserveKeyIndexKey < uint.MaxValue)
            {
                var dontTouchGenerationDueToPreserve = -1L;
                var fileInfo = _keyValueDB.FileCollection.FileInfoByIdx(preserveKeyIndexKey) as IKeyIndex;
                if (fileInfo != null)
                {
                    dontTouchGenerationDueToPreserve = fileInfo.Generation;
                    dontTouchGenerationDueToPreserve = Math.Min(dontTouchGenerationDueToPreserve, _keyValueDB.GetGeneration(fileInfo.TrLogFileId));
                    if (fileInfo.UsedFilesInOlderGenerations == null)
                        _keyValueDB.LoadUsedFilesFromKeyIndex(preserveKeyIndexKey, fileInfo);
                    usedFilesFromOldGenerations = fileInfo.UsedFilesInOlderGenerations;
                }
                dontTouchGeneration = Math.Min(dontTouchGeneration, dontTouchGenerationDueToPreserve);
            }
            var lastCommited = _keyValueDB.LastCommited;
            if (_root != lastCommited) ForbidDeleteOfFilesUsedByStillRunningOldTransaction(_root);
            ForbidDeletePreservingHistory(dontTouchGeneration, usedFilesFromOldGenerations);
            CalculateFileUsefullness(lastCommited);
            MarkTotallyUselessFilesAsUnknown();
            var totalWaste = CalcTotalWaste();
            _keyValueDB.Logger?.CompactionStart(totalWaste);
            if (IsWasteSmall(totalWaste))
            {
                if (_keyValueDB.DistanceFromLastKeyIndex(_root) > (ulong)(_keyValueDB.MaxTrLogFileSize / 4))
                    _keyValueDB.CreateIndexFile(_cancellation, preserveKeyIndexGeneration);
                _keyValueDB.FileCollection.DeleteAllUnknownFiles();
                return false;
            }
            _cancellation.ThrowIfCancellationRequested();
            uint valueFileId;
            var writer = _keyValueDB.StartPureValuesFile(out valueFileId);
            var toRemoveFileIds = new List<uint>();
            _newPositionMap = new Dictionary<ulong, uint>();
            while (true)
            {
                var wastefullFileId = FindMostWastefullFile(_keyValueDB.MaxTrLogFileSize - writer.GetCurrentPosition());
                if (wastefullFileId == 0) break;
                MoveValuesContent(writer, wastefullFileId);
                if (_fileStats[wastefullFileId].IsFreeToDelete())
                    toRemoveFileIds.Add(wastefullFileId);
                _fileStats[wastefullFileId] = new FileStat(0);
            }
            var valueFile = _keyValueDB.FileCollection.GetFile(valueFileId);
            valueFile.HardFlush();
            valueFile.Truncate();
            _keyValueDB.Logger?.CompactionCreatedPureValueFile(valueFileId, valueFile.GetSize());
            var btreesCorrectInTransactionId = _keyValueDB.ReplaceBTreeValues(_cancellation, valueFileId, _newPositionMap);
            _keyValueDB.CreateIndexFile(_cancellation, preserveKeyIndexGeneration);
            if (_newPositionMap.Count == 0)
            {
                toRemoveFileIds.Add(valueFileId);
            }
            if (_keyValueDB.AreAllTransactionsBeforeFinished(btreesCorrectInTransactionId))
            {
                _keyValueDB.MarkAsUnknown(toRemoveFileIds);
            }
            _keyValueDB.FileCollection.DeleteAllUnknownFiles();
            return true;
        }

        void ForbidDeleteOfFilesUsedByStillRunningOldTransaction(IRootNodeInternal root)
        {
            _keyValueDB.IterateRoot(root, (valueFileId, valueOfs, valueSize) =>
            {
                var id = valueFileId;
                var fileStats = _fileStats;
                _cancellation.ThrowIfCancellationRequested();
                if (id < fileStats.Length) fileStats[id].MarkForbidToDelete();
            });
        }

        bool IsWasteSmall(ulong totalWaste)
        {
            return totalWaste < (ulong)_keyValueDB.MaxTrLogFileSize / 4;
        }

        void MoveValuesContent(AbstractBufferedWriter writer, uint wastefullFileId)
        {
            const uint blockSize = 128 * 1024;
            var wasteFullStream = _keyValueDB.FileCollection.GetFile(wastefullFileId);
            var totalSize = wasteFullStream.GetSize();
            var blocks = (int)((totalSize + blockSize - 1) / blockSize);
            var wasteInMemory = new byte[blocks][];
            var pos = 0UL;
            for (var i = 0; i < blocks; i++)
            {
                _cancellation.ThrowIfCancellationRequested();
                wasteInMemory[i] = new byte[blockSize];
                var readSize = totalSize - pos;
                if (readSize > blockSize) readSize = blockSize;
                wasteFullStream.RandomRead(wasteInMemory[i].AsSpan(0, (int)readSize), pos, true);
                pos += readSize;
            }
            _keyValueDB.IterateRoot(_root, (valueFileId, valueOfs, valueSize) =>
                {
                    if (valueFileId != wastefullFileId) return;
                    var size = (uint)Math.Abs(valueSize);
                    _newPositionMap.Add(((ulong)wastefullFileId << 32) | valueOfs, (uint)writer.GetCurrentPosition());
                    pos = valueOfs;
                    while (size > 0)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        var blockId = pos / blockSize;
                        var blockStart = pos % blockSize;
                        var writeSize = (uint)(blockSize - blockStart);
                        if (writeSize > size) writeSize = size;
                        writer.WriteBlock(wasteInMemory[blockId], (int)blockStart, (int)writeSize);
                        size -= writeSize;
                        pos += writeSize;
                    }
                });
        }

        ulong CalcTotalWaste()
        {
            var total = 0ul;
            foreach (var fileStat in _fileStats)
            {
                var waste = fileStat.CalcWasteIgnoreUseless();
                if (waste > 1024) total += waste;
            }
            return total;
        }

        uint FindMostWastefullFile(long space)
        {
            if (space <= 0) return 0;
            var bestWaste = 0u;
            var bestFile = 0u;
            for (var index = 0u; index < _fileStats.Length; index++)
            {
                var waste = _fileStats[index].CalcWasteIgnoreUseless();
                if (waste <= bestWaste || space < _fileStats[index].CalcUsed()) continue;
                bestWaste = waste;
                bestFile = index;
            }
            return bestFile;
        }

        void InitFileStats(long dontTouchGeneration)
        {
            _fileStats = new FileStat[_keyValueDB.FileCollection.FileInfos.Max(f => f.Key) + 1];
            foreach (var file in _keyValueDB.FileCollection.FileInfos)
            {
                if (file.Key >= _fileStats.Length) continue;
                if (file.Value.SubDBId != 0) continue;
                if (!_keyValueDB.ContainsValuesAndDoesNotTouchGeneration(file.Key, dontTouchGeneration)) continue;
                _fileStats[file.Key] = new FileStat((uint)_keyValueDB.FileCollection.GetSize(file.Key));
            }
        }

        void CalculateFileUsefullness(IRootNodeInternal root)
        {
            _keyValueDB.IterateRoot(root, (valueFileId, valueOfs, valueSize) =>
                {
                    var id = valueFileId;
                    var fileStats = _fileStats;
                    _cancellation.ThrowIfCancellationRequested();
                    if (id < fileStats.Length) fileStats[id].AddLength((uint)Math.Abs(valueSize));
                });
        }
    }
}
