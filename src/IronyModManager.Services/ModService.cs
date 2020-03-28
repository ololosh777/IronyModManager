﻿// ***********************************************************************
// Assembly         : IronyModManager.Services
// Author           : Mario
// Created          : 02-24-2020
//
// Last Modified By : Mario
// Last Modified On : 03-28-2020
// ***********************************************************************
// <copyright file="ModService.cs" company="Mario">
//     Mario
// </copyright>
// <summary></summary>
// ***********************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using IronyModManager.DI;
using IronyModManager.IO.Common;
using IronyModManager.Models.Common;
using IronyModManager.Parser.Common;
using IronyModManager.Parser.Common.Args;
using IronyModManager.Parser.Common.Definitions;
using IronyModManager.Parser.Common.Mod;
using IronyModManager.Services.Common;
using IronyModManager.Storage.Common;

namespace IronyModManager.Services
{
    /// <summary>
    /// Class ModService.
    /// Implements the <see cref="IronyModManager.Services.BaseService" />
    /// Implements the <see cref="IronyModManager.Services.Common.IModService" />
    /// </summary>
    /// <seealso cref="IronyModManager.Services.BaseService" />
    /// <seealso cref="IronyModManager.Services.Common.IModService" />
    public class ModService : BaseService, IModService
    {
        #region Fields

        /// <summary>
        /// The service lock
        /// </summary>
        private static readonly object serviceLock = new { };

        /// <summary>
        /// The game service
        /// </summary>
        private readonly IGameService gameService;

        /// <summary>
        /// The mod exporter
        /// </summary>
        private readonly IModExporter modExporter;

        /// <summary>
        /// The mod parser
        /// </summary>
        private readonly IModParser modParser;

        /// <summary>
        /// The parser manager
        /// </summary>
        private readonly IParserManager parserManager;

        /// <summary>
        /// The reader
        /// </summary>
        private readonly IReader reader;

        #endregion Fields

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="ModService" /> class.
        /// </summary>
        /// <param name="reader">The reader.</param>
        /// <param name="parserManager">The parser manager.</param>
        /// <param name="modParser">The mod parser.</param>
        /// <param name="modExporter">The mod exporter.</param>
        /// <param name="gameService">The game service.</param>
        /// <param name="storageProvider">The storage provider.</param>
        /// <param name="mapper">The mapper.</param>
        public ModService(IReader reader, IParserManager parserManager,
            IModParser modParser, IModExporter modExporter,
            IGameService gameService, IStorageProvider storageProvider,
            IMapper mapper) : base(storageProvider, mapper)
        {
            this.reader = reader;
            this.parserManager = parserManager;
            this.modParser = modParser;
            this.modExporter = modExporter;
            this.gameService = gameService;
        }

        #endregion Constructors

        #region Events

        /// <summary>
        /// Occurs when [mod definition analyze].
        /// </summary>
        public event ModDefinitionAnalyzeDelegate ModDefinitionAnalyze;

        /// <summary>
        /// Occurs when [mod analyze].
        /// </summary>
        public event ModDefinitionLoadDelegate ModDefinitionLoad;

        #endregion Events

        #region Methods

        /// <summary>
        /// Builds the mod URL.
        /// </summary>
        /// <param name="mod">The mod.</param>
        /// <returns>System.String.</returns>
        public string BuildModUrl(IMod mod)
        {
            if (!mod.RemoteId.HasValue)
            {
                return string.Empty;
            }
            return mod.Source switch
            {
                ModSource.Steam => string.Format(Constants.Steam_Url, mod.RemoteId),
                ModSource.Paradox => string.Format(Constants.Paradox_Url, mod.RemoteId),
                _ => string.Empty,
            };
        }

        /// <summary>
        /// Exports the mods asynchronous.
        /// </summary>
        /// <param name="mods">The mods.</param>
        /// <returns>Task&lt;System.Boolean&gt;.</returns>
        public Task<bool> ExportModsAsync(IReadOnlyCollection<IMod> mods)
        {
            var game = gameService.GetSelected();
            if (game == null || mods == null)
            {
                return Task.FromResult(false);
            }
            return modExporter.ApplyCollectionAsync(mods, game.UserDirectory);
        }

        /// <summary>
        /// Finds the conflicts.
        /// </summary>
        /// <param name="indexedDefinitions">The indexed definitions.</param>
        /// <returns>IIndexedDefinitions.</returns>
        public virtual IConflictResult FindConflicts(IIndexedDefinitions indexedDefinitions)
        {
            var conflicts = new HashSet<IDefinition>();
            var fileKeys = indexedDefinitions.GetAllFileKeys();
            var typeAndIdKeys = indexedDefinitions.GetAllTypeAndIdKeys();

            double total = fileKeys.Count() + typeAndIdKeys.Count();
            double processed = 0;

            foreach (var item in fileKeys)
            {
                var definitions = indexedDefinitions.GetByFile(item);
                EvalDefinitions(indexedDefinitions, conflicts, definitions);
                processed++;
                ModDefinitionAnalyze?.Invoke(Convert.ToInt32(processed / total * 100));
            }

            foreach (var item in typeAndIdKeys)
            {
                var definitions = indexedDefinitions.GetByTypeAndId(item);
                EvalDefinitions(indexedDefinitions, conflicts, definitions);
                processed++;
                ModDefinitionAnalyze?.Invoke(Convert.ToInt32(processed / total * 100));
            }

            var groupedConflicts = conflicts.GroupBy(p => p.TypeAndId);
            var result = GetModelInstance<IConflictResult>();
            var conflictsIndexed = DIResolver.Get<IIndexedDefinitions>();
            conflictsIndexed.InitMap(groupedConflicts.Where(p => p.Count() > 1).SelectMany(p => p), true);
            var orphanedConflictsIndexed = DIResolver.Get<IIndexedDefinitions>();
            orphanedConflictsIndexed.InitMap(groupedConflicts.Where(p => p.Count() == 1).SelectMany(p => p), true);
            result.AllConflicts = indexedDefinitions;
            result.Conflicts = conflictsIndexed;
            result.OrphanConflicts = orphanedConflictsIndexed;

            return result;
        }

        /// <summary>
        /// Gets the definitions to write.
        /// </summary>
        /// <param name="conflictResult">The conflict result.</param>
        /// <param name="definition">The definition.</param>
        /// <returns>IEnumerable&lt;IDefinition&gt;.</returns>
        public virtual IEnumerable<IDefinition> GetDefinitionsToWrite(IConflictResult conflictResult, IDefinition definition)
        {
            if (definition.ValueType != Parser.Common.ValueType.Object)
            {
                return new List<IDefinition>() { definition };
            }
            var definitions = new List<IDefinition>() { definition };
            var conflicts = FilterValidWriteDefinitions(conflictResult.Conflicts, definition);
            if (conflicts.Count() > 0)
            {
                // TODO: Will need to handle overrides here
            }
            var allConflicts = FilterValidWriteDefinitions(conflictResult.AllConflicts, definition).Where(p => !conflicts.Any(c => c.Id.Equals(p.Id)));
            definitions.AddRange(allConflicts.GroupBy(p => p.Id).Select(p => p.First()));
            var orphanConflicts = FilterValidWriteDefinitions(conflictResult.OrphanConflicts, definition).Where(p => !allConflicts.Any(c => c.Id.Equals(p.Id)));
            definitions.AddRange(orphanConflicts.GroupBy(p => p.Id).Select(p => p.First()));
            return definitions;
        }

        /// <summary>
        /// Gets the installed mods.
        /// </summary>
        /// <param name="game">The game.</param>
        /// <returns>IEnumerable&lt;IModObject&gt;.</returns>
        /// <exception cref="ArgumentNullException">game</exception>
        public virtual IEnumerable<IMod> GetInstalledMods(IGame game)
        {
            if (game == null)
            {
                throw new ArgumentNullException("game");
            }
            var result = new List<IMod>();
            var installedMods = reader.Read(Path.Combine(game.UserDirectory, Constants.ModDirectory));
            if (installedMods?.Count() > 0)
            {
                foreach (var installedMod in installedMods)
                {
                    var mod = Mapper.Map<IMod>(modParser.Parse(installedMod.Content));
                    mod.DescriptorFile = $"{Constants.ModDirectory}/{installedMod.FileName}";
                    mod.Source = GetModSource(installedMod);
                    if (mod.Source == ModSource.Paradox)
                    {
                        mod.RemoteId = GetPdxModId(installedMod.FileName);
                    }
                    result.Add(mod);
                }
            }
            return result;
        }

        /// <summary>
        /// Gets the mod objects.
        /// </summary>
        /// <param name="game">The game.</param>
        /// <param name="mods">The mods.</param>
        /// <returns>IIndexedDefinitions.</returns>
        public virtual IIndexedDefinitions GetModObjects(IGame game, IEnumerable<IMod> mods)
        {
            if (game == null || mods == null || mods.Count() == 0)
            {
                return null;
            }
            var definitions = new ConcurrentBag<IDefinition>();

            double processed = 0;
            double total = mods.Count();

            mods.AsParallel().ForAll((m) =>
            {
                IEnumerable<IDefinition> result = null;
                if (Path.IsPathFullyQualified(m.FileName))
                {
                    result = ParseModFiles(game, reader.Read(m.FileName), m);
                }
                else
                {
                    // Check user directory and workshop directory.
                    // Technically we don't need this since newer pdx mod launchers use absolute paths.
                    // IronyModManager will always require that a user runs the PDX mod launcher first when new mods are installed.
                    // This program will not be a replacement for mod installation only for mod management.
                    var userDirectoryMod = Path.Combine(game.UserDirectory, m.FileName);
                    var workshopDirectoryMod = Path.Combine(game.WorkshopDirectory, m.FileName);
                    if (File.Exists(userDirectoryMod) || Directory.Exists(userDirectoryMod))
                    {
                        result = ParseModFiles(game, reader.Read(userDirectoryMod), m);
                    }
                    else if (File.Exists(workshopDirectoryMod) || Directory.Exists(workshopDirectoryMod))
                    {
                        result = ParseModFiles(game, reader.Read(workshopDirectoryMod), m);
                    }
                }
                if (result?.Count() > 0)
                {
                    foreach (var item in result)
                    {
                        definitions.Add(item);
                    }
                }
                lock (serviceLock)
                {
                    processed++;
                    var perc = Convert.ToInt32((processed / total * 100) - 2);
                    if (perc < 0)
                    {
                        perc = 1;
                    }
                    ModDefinitionLoad?.Invoke(perc);
                }
            });

            ModDefinitionLoad?.Invoke(99);
            var indexed = DIResolver.Get<IIndexedDefinitions>();
            indexed.InitMap(definitions);
            ModDefinitionLoad?.Invoke(100);
            return indexed;
        }

        /// <summary>
        /// Evals the definitions.
        /// </summary>
        /// <param name="indexedDefinitions">The indexed definitions.</param>
        /// <param name="conflicts">The conflicts.</param>
        /// <param name="definitions">The definitions.</param>
        protected virtual void EvalDefinitions(IIndexedDefinitions indexedDefinitions, HashSet<IDefinition> conflicts, IEnumerable<IDefinition> definitions)
        {
            if (definitions.GroupBy(p => p.ModName.ToLowerInvariant()).Count() > 1)
            {
                var processed = new HashSet<IDefinition>();
                foreach (var def in definitions)
                {
                    if (processed.Contains(def) || conflicts.Contains(def))
                    {
                        continue;
                    }
                    var allConflicts = indexedDefinitions.GetByTypeAndId(def.Type, def.Id);
                    foreach (var conflict in allConflicts)
                    {
                        processed.Add(conflict);
                    }
                    if (allConflicts.Count() > 1)
                    {
                        if (!allConflicts.All(p => p.DefinitionSHA.Equals(def.DefinitionSHA)))
                        {
                            var validConflicts = new HashSet<IDefinition>();
                            foreach (var conflict in allConflicts)
                            {
                                if (conflicts.Contains(conflict) || validConflicts.Contains(conflict))
                                {
                                    continue;
                                }
                                var hasOverrides = allConflicts.Any(p => (p.Dependencies?.Any(p => p.Contains(conflict.ModName))).GetValueOrDefault());
                                if (hasOverrides)
                                {
                                    continue;
                                }
                                validConflicts.Add(conflict);
                            }

                            var validConflictsGroup = validConflicts.GroupBy(p => p.DefinitionSHA);
                            if (validConflictsGroup.Count() > 1)
                            {
                                var filteredConflicts = validConflictsGroup.Select(p => p.OrderBy(p => p.ModName).First());
                                foreach (var item in filteredConflicts)
                                {
                                    if (!conflicts.Contains(item) && item.ValueType != Parser.Common.ValueType.Namespace)
                                    {
                                        conflicts.Add(item);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (!conflicts.Contains(def) && def.ValueType != Parser.Common.ValueType.Namespace)
                        {
                            conflicts.Add(def);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Filters the valid write definitions.
        /// </summary>
        /// <param name="indexedDefinitions">The indexed definitions.</param>
        /// <param name="definition">The definition.</param>
        /// <returns>IEnumerable&lt;IDefinition&gt;.</returns>
        protected virtual IEnumerable<IDefinition> FilterValidWriteDefinitions(IIndexedDefinitions indexedDefinitions, IDefinition definition)
        {
            var conflicts = indexedDefinitions.GetByFile(definition.File);
            if (conflicts != null)
            {
                return conflicts.Where(p => !p.Id.Equals(definition.Id) && p.ValueType == Parser.Common.ValueType.Variable || p.ValueType == Parser.Common.ValueType.Namespace);
            }
            return new List<IDefinition>();
        }

        /// <summary>
        /// Gets the mod source.
        /// </summary>
        /// <param name="fileInfo">The file information.</param>
        /// <returns>ModSource.</returns>
        protected virtual ModSource GetModSource(IFileInfo fileInfo)
        {
            if (fileInfo.FileName.Contains(Constants.Paradox_mod_id))
            {
                return ModSource.Paradox;
            }
            else if (fileInfo.FileName.Contains(Constants.Steam_mod_id))
            {
                return ModSource.Steam;
            }
            return ModSource.Local;
        }

        /// <summary>
        /// Gets the PDX mod identifier.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>System.Int32.</returns>
        protected virtual int GetPdxModId(string filename)
        {
            var name = Path.GetFileNameWithoutExtension(filename);
            int.TryParse(name.Replace(Constants.Paradox_mod_id, string.Empty), out var id);
            return id;
        }

        /// <summary>
        /// Parses the mod files.
        /// </summary>
        /// <param name="game">The game.</param>
        /// <param name="fileInfos">The file infos.</param>
        /// <param name="modObject">The mod object.</param>
        /// <returns>IEnumerable&lt;IDefinition&gt;.</returns>
        protected virtual IEnumerable<IDefinition> ParseModFiles(IGame game, IEnumerable<IFileInfo> fileInfos, IModObject modObject)
        {
            var definitions = new List<IDefinition>();
            foreach (var fileInfo in fileInfos)
            {
                definitions.AddRange(parserManager.Parse(new ParserManagerArgs()
                {
                    ContentSHA = fileInfo.ContentSHA,
                    File = fileInfo.FileName,
                    GameType = game.Type,
                    Lines = fileInfo.Content,
                    ModDependencies = modObject.Dependencies,
                    ModName = modObject.Name
                }));
            }
            return definitions;
        }

        #endregion Methods
    }
}
