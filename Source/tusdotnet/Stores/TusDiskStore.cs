﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using tusdotnet.Extensions;
using tusdotnet.Helpers;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Concatenation;
using tusdotnet.Stores.FileIdProviders;

namespace tusdotnet.Stores
{
    /// <summary>
    /// The built in data store that save files on disk.
    /// </summary>
    public partial class TusDiskStore
        : ITusStore,
#if pipelines
            ITusPipelineStore,
#endif
            ITusCreationStore,
            ITusReadableStore,
            ITusTerminationStore,
            ITusChecksumStore,
            ITusConcatenationStore,
            ITusExpirationStore,
            ITusCreationDeferLengthStore
    {
        private readonly string _directoryPath;
        private readonly bool _deletePartialFilesOnConcat;
        private readonly InternalFileRep.FileRepFactory _fileRepFactory;
        private readonly ITusFileIdProvider _fileIdProvider;

        // These are the read and write buffer sizes, they will get the value of TusDiskBufferSize.Default if not set in the constructor.
        private readonly int _maxReadBufferSize;
        private readonly int _maxWriteBufferSize;

        // Use our own array pool to not leak data to other parts of the running app.
        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Create();

        private static readonly GuidFileIdProvider _defaultFileIdProvider = new();

        private static readonly IEnumerable<string> _supportedAlgorithms = ["sha1"];

        /// <summary>
        /// Initializes a new instance of the <see cref="TusDiskStore"/> class.
        /// Using this overload will not delete partial files if a final concatenation is performed.
        /// </summary>
        /// <param name="directoryPath">The path on disk where to save files</param>
        public TusDiskStore(string directoryPath)
            : this(directoryPath, false, TusDiskBufferSize.Default)
        {
            // Left blank.
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TusDiskStore"/> class.
        /// </summary>
        /// <param name="directoryPath">The path on disk where to save files</param>
        /// <param name="deletePartialFilesOnConcat">True to delete partial files if a final concatenation is performed</param>
        public TusDiskStore(string directoryPath, bool deletePartialFilesOnConcat)
            : this(directoryPath, deletePartialFilesOnConcat, TusDiskBufferSize.Default)
        {
            // Left blank.
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TusDiskStore"/> class.
        /// </summary>
        /// <param name="directoryPath">The path on disk where to save files</param>
        /// <param name="deletePartialFilesOnConcat">True to delete partial files if a final concatenation is performed</param>
        /// <param name="bufferSize">The buffer size to use when reading and writing. If unsure use <see cref="TusDiskBufferSize.Default"/>.</param>
        public TusDiskStore(
            string directoryPath,
            bool deletePartialFilesOnConcat,
            TusDiskBufferSize bufferSize
        )
            : this(directoryPath, deletePartialFilesOnConcat, bufferSize, _defaultFileIdProvider)
        {
            // Left blank.
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TusDiskStore"/> class.
        /// </summary>
        /// <param name="directoryPath">The path on disk where to save files</param>
        /// <param name="deletePartialFilesOnConcat">True to delete partial files if a final concatenation is performed</param>
        /// <param name="bufferSize">The buffer size to use when reading and writing. If unsure use <see cref="TusDiskBufferSize.Default"/>.</param>
        /// <param name="fileIdProvider">The provider that generates ids for files. If unsure use <see cref="GuidFileIdProvider"/>.</param>
        public TusDiskStore(
            string directoryPath,
            bool deletePartialFilesOnConcat,
            TusDiskBufferSize bufferSize,
            ITusFileIdProvider fileIdProvider
        )
        {
            _directoryPath = directoryPath;
            _deletePartialFilesOnConcat = deletePartialFilesOnConcat;
            _fileRepFactory = new InternalFileRep.FileRepFactory(_directoryPath);

            bufferSize ??= TusDiskBufferSize.Default;

            _maxWriteBufferSize = bufferSize.WriteBufferSizeInBytes;
            _maxReadBufferSize = bufferSize.ReadBufferSizeInBytes;

            _fileIdProvider = fileIdProvider;
        }

        /// <inheritdoc />
        public async Task<bool> FileExistAsync(string fileId, CancellationToken _)
        {
            return _fileRepFactory
                .Data(await InternalFileId.Parse(_fileIdProvider, fileId))
                .Exist();
        }

        /// <inheritdoc />
        public async Task<long?> GetUploadLengthAsync(string fileId, CancellationToken _)
        {
            var firstLine = _fileRepFactory
                .UploadLength(await InternalFileId.Parse(_fileIdProvider, fileId))
                .ReadFirstLine(true);
            return firstLine == null ? null : long.Parse(firstLine);
        }

        /// <inheritdoc />
        public async Task<long> GetUploadOffsetAsync(string fileId, CancellationToken _)
        {
            return _fileRepFactory
                .Data(await InternalFileId.Parse(_fileIdProvider, fileId))
                .GetLength();
        }

        /// <inheritdoc />
        public async Task<string> CreateFileAsync(
            long uploadLength,
            string metadata,
            CancellationToken _
        )
        {
            var fileId = await InternalFileId.CreateNew(_fileIdProvider, metadata);
            new FileStream(_fileRepFactory.Data(fileId).Path, FileMode.CreateNew).Dispose();
            if (uploadLength != -1)
            {
                await SetUploadLengthAsync(fileId, uploadLength, CancellationToken.None);
            }
            _fileRepFactory.Metadata(fileId).Write(metadata);
            return fileId;
        }

        /// <inheritdoc />
        public async Task<string> GetUploadMetadataAsync(string fileId, CancellationToken _)
        {
            var firstLine = _fileRepFactory
                .Metadata(await InternalFileId.Parse(_fileIdProvider, fileId))
                .ReadFirstLine(true);
            return string.IsNullOrEmpty(firstLine) ? null : firstLine;
        }

        /// <inheritdoc />
        public async Task<ITusFile> GetFileAsync(string fileId, CancellationToken _)
        {
            var internalFileId = await InternalFileId.Parse(_fileIdProvider, fileId);
            var data = _fileRepFactory.Data(internalFileId);

            return data.Exist()
                ? new TusDiskFile(data, _fileRepFactory.Metadata(internalFileId))
                : null;
        }

        /// <inheritdoc />
        public async Task DeleteFileAsync(string fileId, CancellationToken _)
        {
            var internalFileId = await InternalFileId.Parse(_fileIdProvider, fileId);
            await Task.Run(
                () =>
                {
                    _fileRepFactory.Data(internalFileId).Delete();
                    _fileRepFactory.UploadLength(internalFileId).Delete();
                    _fileRepFactory.Metadata(internalFileId).Delete();
                    _fileRepFactory.UploadConcat(internalFileId).Delete();
                    _fileRepFactory.ChunkStartPosition(internalFileId).Delete();
                    _fileRepFactory.ChunkComplete(internalFileId).Delete();
                    _fileRepFactory.Expiration(internalFileId).Delete();
                },
                CancellationToken.None
            );
        }

        /// <inheritdoc />
        public Task<IEnumerable<string>> GetSupportedAlgorithmsAsync(CancellationToken _)
        {
            return Task.FromResult(_supportedAlgorithms);
        }

        /// <inheritdoc />
        public async Task<bool> VerifyChecksumAsync(
            string fileId,
            string algorithm,
            byte[] checksum,
            CancellationToken _
        )
        {
            var valid = false;
            var internalFileId = await InternalFileId.Parse(_fileIdProvider, fileId);

            using var dataStream = _fileRepFactory
                .Data(internalFileId)
                .GetStream(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

            var chunkStartPosition = _fileRepFactory
                .ChunkStartPosition(internalFileId)
                .ReadFirstLineAsLong(true, 0);

            var chunkCompleteFile = _fileRepFactory.ChunkComplete(internalFileId);

            // If the client has provided a faulty checksum-trailer we should just discard the chunk.
            // Otherwise only verify the checksum if the entire lastest chunk has been written.
            // If not, just discard the last chunk as it won't match the checksum anyway.
            if (!ChecksumTrailerHelper.IsFallback(algorithm, checksum) && chunkCompleteFile.Exist())
            {
                var calculatedChecksum = chunkCompleteFile.ReadBytes();

                // If we don't have the optimized checksum file calculate it from the file stream.
                if (calculatedChecksum is { Length: 1 })
                {
                    calculatedChecksum = dataStream.CalculateSha1(chunkStartPosition);
                }

                valid = checksum.SequenceEqual(calculatedChecksum);
            }

            if (!valid)
            {
                dataStream.SetLength(chunkStartPosition);
            }

            return valid;
        }

        /// <inheritdoc />
        public async Task<FileConcat> GetUploadConcatAsync(string fileId, CancellationToken _)
        {
            var firstLine = _fileRepFactory
                .UploadConcat(await InternalFileId.Parse(_fileIdProvider, fileId))
                .ReadFirstLine(true);
            return string.IsNullOrWhiteSpace(firstLine) ? null : new UploadConcat(firstLine).Type;
        }

        /// <inheritdoc />
        public async Task<string> CreatePartialFileAsync(
            long uploadLength,
            string metadata,
            CancellationToken _
        )
        {
            var fileId = await CreateFileAsync(uploadLength, metadata, CancellationToken.None);
            _fileRepFactory
                .UploadConcat(await InternalFileId.Parse(_fileIdProvider, fileId))
                .Write(new FileConcatPartial().GetHeader());
            return fileId;
        }

        /// <inheritdoc />
        public async Task<string> CreateFinalFileAsync(
            string[] partialFiles,
            string metadata,
            CancellationToken _
        )
        {
            InternalFileRep[] partialInternalFileReps = new InternalFileRep[partialFiles.Length];
            for (int i = 0; i < partialFiles.Length; i++)
            {
                partialInternalFileReps[i] = _fileRepFactory.Data(
                    await InternalFileId.Parse(_fileIdProvider, partialFiles[i])
                );

                if (!partialInternalFileReps[i].Exist())
                {
                    throw new TusStoreException($"File {partialFiles[i]} does not exist");
                }
            }

            var length = partialInternalFileReps.Sum(f => f.GetLength());

            var fileId = await CreateFileAsync(length, metadata, CancellationToken.None);

            var internalFileId = await InternalFileId.Parse(_fileIdProvider, fileId);

            _fileRepFactory
                .UploadConcat(internalFileId)
                .Write(new FileConcatFinal(partialFiles).GetHeader());

            using (
                var finalFile = _fileRepFactory
                    .Data(internalFileId)
                    .GetStream(FileMode.Open, FileAccess.Write, FileShare.None)
            )
            {
                foreach (var partialFile in partialInternalFileReps)
                {
                    using var partialStream = partialFile.GetStream(
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    );

                    await partialStream.CopyToAsync(finalFile);
                }
            }

            if (_deletePartialFilesOnConcat)
            {
                await Task.WhenAll(
                    partialInternalFileReps.Select(f =>
                        DeleteFileAsync(f.FileId, CancellationToken.None)
                    )
                );
            }

            return fileId;
        }

        /// <inheritdoc />
        public async Task SetExpirationAsync(
            string fileId,
            DateTimeOffset expires,
            CancellationToken _
        )
        {
            _fileRepFactory
                .Expiration(await InternalFileId.Parse(_fileIdProvider, fileId))
                .Write(expires.ToString("O"));
        }

        /// <inheritdoc />
        public async Task<DateTimeOffset?> GetExpirationAsync(string fileId, CancellationToken _)
        {
            var expiration = _fileRepFactory
                .Expiration(await InternalFileId.Parse(_fileIdProvider, fileId))
                .ReadFirstLine(true);

            return expiration == null ? null : DateTimeOffset.ParseExact(expiration, "O", null);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<string>> GetExpiredFilesAsync(CancellationToken _)
        {
            var expiredFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_directoryPath, "*.expiration"))
            {
                var f = await InternalFileId.Parse(
                    _fileIdProvider,
                    Path.GetFileNameWithoutExtension(file)
                );
                if (FileHasExpired(f, _fileRepFactory) && FileIsIncomplete(f, _fileRepFactory))
                {
                    expiredFiles.Add(f);
                }
            }

            return expiredFiles;

            static bool FileHasExpired(
                InternalFileId fileId,
                InternalFileRep.FileRepFactory fileRepFactory
            )
            {
                var firstLine = fileRepFactory.Expiration(fileId).ReadFirstLine();
                return !string.IsNullOrWhiteSpace(firstLine)
                    && DateTimeOffset.ParseExact(firstLine, "O", null).HasPassed();
            }

            static bool FileIsIncomplete(
                InternalFileId fileId,
                InternalFileRep.FileRepFactory fileRepFactory
            )
            {
                var uploadLength = fileRepFactory
                    .UploadLength(fileId)
                    .ReadFirstLineAsLong(fileIsOptional: true, defaultValue: long.MinValue);

                if (uploadLength == long.MinValue)
                {
                    return true;
                }

                var dataFile = fileRepFactory.Data(fileId);

                if (!dataFile.Exist())
                {
                    return true;
                }

                return uploadLength != dataFile.GetLength();
            }
        }

        /// <inheritdoc />
        public async Task<int> RemoveExpiredFilesAsync(CancellationToken _)
        {
            var expiredFiles = await GetExpiredFilesAsync(CancellationToken.None);
            var deleteFileTasks = expiredFiles
                .Select(file => DeleteFileAsync(file, CancellationToken.None))
                .ToList();

            await Task.WhenAll(deleteFileTasks);

            return deleteFileTasks.Count;
        }

        /// <inheritdoc />
        public async Task SetUploadLengthAsync(
            string fileId,
            long uploadLength,
            CancellationToken _
        )
        {
            _fileRepFactory
                .UploadLength(await InternalFileId.Parse(_fileIdProvider, fileId))
                .Write(uploadLength.ToString());
        }

        private InternalFileRep InitializeChunk(
            InternalFileId internalFileId,
            long totalDiskFileLength
        )
        {
            var chunkComplete = _fileRepFactory.ChunkComplete(internalFileId);
            chunkComplete.Delete();

            _fileRepFactory
                .ChunkStartPosition(internalFileId)
                .Write(totalDiskFileLength.ToString());

            return chunkComplete;
        }

        private static void MarkChunkComplete(InternalFileRep chunkComplete, byte[] checksum)
        {
            chunkComplete.Write(checksum ?? DefaultValueForChunkComplete);
        }

        // The string "1" due to backwards compatibility. Keep as static byte[] to not reallocate.
        private static readonly byte[] DefaultValueForChunkComplete = [49];
    }
}
