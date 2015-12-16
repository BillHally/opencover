﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OpenCover.Framework.Communication;
using OpenCover.Framework.Model;
using OpenCover.Framework.Utility;
using log4net;

namespace OpenCover.Framework.Persistance
{
    /// <summary>
    /// A basic layer that aggregates the data
    /// </summary>
    public abstract class BasePersistance : IPersistance
    {
        protected readonly ICommandLine CommandLine;
        private readonly ILog _logger;
        private uint _trackedMethodId;
        private readonly Dictionary<Module, Dictionary<int, KeyValuePair<Class, Method>>> _moduleMethodMap = new Dictionary<Module, Dictionary<int, KeyValuePair<Class, Method>>>();

        private static readonly ILog DebugLogger = LogManager.GetLogger("DebugLogger");

        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="commandLine"></param>
        /// <param name="logger"></param>
        protected BasePersistance(ICommandLine commandLine, ILog logger)
        {
            CommandLine = commandLine;
            _logger = logger ?? DebugLogger;
            CoverageSession = new CoverageSession();
            _trackedMethodId = 0;
        }

        /// <summary>
        /// A coverage session
        /// </summary>
        public CoverageSession CoverageSession { get; private set; }

        /// <summary>
        /// Add the <see cref="Module"/> to the current session
        /// </summary>
        /// <param name="module"></param>
        public void PersistModule(Module module)
        {
            if (module == null) return;
            module.Classes = module.Classes ?? new Class[0];
            if (CommandLine.MergeByHash)
            {
                var modules = CoverageSession.Modules ?? new Module[0];
                var existingModule = modules.FirstOrDefault(x => x.ModuleHash == module.ModuleHash);
                if (existingModule!=null)
                {
                    if (!existingModule.Aliases.Any(x=>x.Equals(module.FullName, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        existingModule.Aliases.Add(module.FullName);
                    }
                    return;
                }
            }

            _moduleMethodMap[module] = new Dictionary<int, KeyValuePair<Class, Method>>();
            BuildMethodMapForModule(module);
            var list = new List<Module>(CoverageSession.Modules ?? new Module[0]) { module };
            CoverageSession.Modules = list.ToArray();
        }

        /// <summary>
        /// Clear the current coverage session data
        /// </summary>
        protected void ClearCoverageSession()
        {
            _moduleMethodMap.Clear();
            CoverageSession = new CoverageSession();
            InstrumentationPoint.Clear();
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="session"></param>
        protected void ReassignCoverageSession(CoverageSession session)
        {
            _moduleMethodMap.Clear();
            CoverageSession = session;
            CoverageSession.Summary = new Summary();
            foreach (var module in CoverageSession.Modules)
            {
                BuildMethodMapForModule(module);
                module.Summary = new Summary();
                foreach (var @class in module.Classes)
                {
                    @class.Summary = new Summary();
                    foreach (var method in @class.Methods)
                    {
                        method.Summary = new Summary();
                        if (method.SequencePoints != null && method.SequencePoints.Any() && method.SequencePoints[0].Offset == method.MethodPoint.Offset)
                        {
                            var point = new[] { method.SequencePoints[0], (SequencePoint)method.MethodPoint }
                                .OrderBy(x => x.OrigSequencePoint)
                                .First();

                            method.MethodPoint = point;
                            method.SequencePoints[0] = point;
                        }
                    }
                }
            }

            InstrumentationPoint.ResetAfterLoading();
            File.ResetAfterLoading();
        }

        private void BuildMethodMapForModule(Module module)
        {
            _moduleMethodMap[module] = new Dictionary<int, KeyValuePair<Class, Method>>();
            foreach (var @class in module.Classes)
            {
                foreach (var method in @class.Methods)
                {
                    _moduleMethodMap[module][method.MetadataToken] = new KeyValuePair<Class, Method>(@class, method);
                }
            }
        }

        /// <summary>
        /// Is the module being tracked
        /// </summary>
        /// <param name="modulePath"></param>
        /// <returns></returns>
        public bool IsTracking(string modulePath)
        {
            return CoverageSession.Modules.Any(x => x.Aliases.Any(path => path.Equals(modulePath, StringComparison.InvariantCultureIgnoreCase)) &&
                    !x.ShouldSerializeSkippedDueTo());
        }

        /// <summary>
        /// we are done and the data needs one last clean up
        /// </summary>
        public virtual void Commit()
        {
            PopulateInstrumentedPoints();

            if (CommandLine.HideSkipped == null) return;
            
            if (!CommandLine.HideSkipped.Any()) return;
            
            foreach (var skippedMethod in CommandLine.HideSkipped.OrderBy(x => x))
            {
                switch (skippedMethod)
                {
                    case SkippedMethod.File:
                        RemoveSkippedMethods(SkippedMethod.File);
                        RemoveEmptyClasses();
                        RemoveUnreferencedFiles();
                        break;
                    case SkippedMethod.Filter:
                        RemoveSkippedModules(SkippedMethod.Filter);
                        RemoveSkippedClasses(SkippedMethod.Filter);
                        break;
                    case SkippedMethod.MissingPdb:
                        RemoveSkippedModules(SkippedMethod.MissingPdb);
                        break;
                    case SkippedMethod.Attribute:
                        RemoveSkippedClasses(SkippedMethod.Attribute);
                        RemoveSkippedMethods(SkippedMethod.Attribute);
                        RemoveEmptyClasses();
                        break;
                    case SkippedMethod.AutoImplementedProperty:
                        RemoveSkippedMethods(SkippedMethod.Attribute);
                        RemoveEmptyClasses();
                        break;
                }
            }
        }

        private void RemoveSkippedModules(SkippedMethod skipped)
        {
            if (CoverageSession.Modules == null) return;
            var modules = CoverageSession.Modules;
            modules = modules.Where(x => x.SkippedDueTo != skipped).ToArray();
            CoverageSession.Modules = modules;
        }

        private void RemoveSkippedClasses(SkippedMethod skipped)
        {
            if (CoverageSession.Modules == null) return;
            foreach (var module in CoverageSession.Modules)
            {
                if (module.Classes == null) continue;
                var classes = module.Classes.Where(x => x.SkippedDueTo != skipped).ToArray();
                module.Classes = classes;
            }
        }

        private void RemoveSkippedMethods(SkippedMethod skipped)
        {
            if (CoverageSession.Modules == null) return;
            foreach (var module in CoverageSession.Modules)
            {
                if (module.Classes == null) continue;
                foreach (var @class in module.Classes)
                {
                    if (@class.Methods == null) continue;
                    var methods = @class.Methods.Where(x => x.SkippedDueTo != skipped).ToArray();
                    @class.Methods = methods;
                }
            }
        }

        private void RemoveEmptyClasses()
        {
            if (CoverageSession.Modules == null) return;
            foreach (var module in CoverageSession.Modules)
            {
                if (module.Classes == null) continue;
                module.Classes = module.Classes.Where(@class => @class.Methods != null && @class.Methods.Any()).ToArray();
            }
        }

        private void RemoveUnreferencedFiles()
        {
            if (CoverageSession.Modules == null) return;
            foreach (var module in CoverageSession.Modules)
            {
                module.Files = (from file in module.Files ?? new File[0]
                                from @class in module.Classes ?? new Class[0]
                                where (@class.Methods ?? new Method[0]).Where(x=>x.FileRef != null).Any(x => x.FileRef.UniqueId == file.UniqueId)
                                select file).Distinct().ToArray();
            }
        }

        // static readonly empty collections, saves creation time of new empty ones
        private static readonly SequencePoint[] emptySeqPoints = new SequencePoint[0];
        private static readonly BranchPoint[] emptyBranchPoints = new BranchPoint[0];
        private static readonly List<BranchPoint> emptyBranchList = new List<BranchPoint>(0);

        // Dictionary with stored source files per module
        private Dictionary<uint, CodeCoverageStringTextSource> sourceRepository = new Dictionary<uint, CodeCoverageStringTextSource>();

        // match Contract.Requires<*> (
        private static Regex cRequiresThrowMatch = new Regex (@"Contract\s*\.\s*Requires\s*<.+>\s*\(", RegexOptions.Compiled);
        // match Contract.Invariant (
        private static Regex cInvariantMatch = new Regex (@"Contract\s*\.\s*Invariant\s*\(", RegexOptions.Compiled);
        // match Contract.Ensures[OnThrow<*>] (
        private static Regex cEnsuresMatch = new Regex (@"Contract\s*\.\s*Ensures\s*<.+>\s*\(", RegexOptions.Compiled);
        // match Contract.EnsuresOnThrow<*>] (
        private static Regex cEnsuresOnThrowMatch = new Regex (@"Contract\s*\.\s*EnsuresOnThrow\s*<.+>\s*\(", RegexOptions.Compiled);

        private const bool doRemove = true;
        private const bool preserve = false;

        //private static readonly ILog Logger = LogManager.GetLogger("OpenCover");

        // Return true if branches can be removed
        private bool doRemoveBranches (SequencePoint sp) {

            if (sp == null)
                return preserve;
            if (sp.FileId == 0)
                return preserve;

            CodeCoverageStringTextSource source = null;
            sourceRepository.TryGetValue (sp.FileId, out source);
            if (source == null)
                return preserve;

            switch (source.FileType) {
                case FileType.Unsupported:
                    return preserve;
                case FileType.CSharp:
                    break; // continue
                case FileType.VBasic:
                    return preserve;
                default:
#if DEBUG
                    throw new NotImplementedException ("Source.FileType");
#else
                    return preserve;
#endif
            }

            /* Must exclude only SequencePoints that cannot have user defined branches  
             * Another approach by including (selecting) only lines with branches
             * cannot be done by Regex for at least one reason
             * 1) "for" keyword has no SequencePoint (undetectable),
             *    "for" branch is in second SequencePoint "[condition]" of 
             *    "for ( [initialisation] ; [condition] ; [iteration] ) { ....
             *    and "[condition]" can be a boolean function (boolean undetectable by Regex)
            */ 
            string spSource = source.GetText (sp);
            if (String.IsNullOrWhiteSpace (spSource))
                return doRemove;

            switch (spSource.Length) {
                case 1:
                    if (spSource == "{" || spSource == "}") return doRemove;
                    break;
                default:
                    // contract.
                    //          requires<T>(x);
                    //          invariant(x);
                    //          ensures(x);
                    //          ensuresOnThrow<T>(x);
                    // 12345678901234567890
                    if (spSource.Length >= 20 && spSource.Substring(0, 8) == "Contract" && spSource.Last() == ';') {
                        // Requires<Exception> and EnsuresOnThrow<Exception> branches are testable
                        // Requires sometimes has too much branches?
                        if (cInvariantMatch.IsMatch (spSource))
                            return doRemove;
                        if (cEnsuresMatch.IsMatch (spSource))
                            return doRemove;
                    }
                    break;
            }
            return preserve;
        }


        private void PopulateInstrumentedPoints()
        {

            if (CoverageSession.Modules == null) return;

            foreach (var method in from @class in
                                       (from module in CoverageSession.Modules
                                        from @class in module.Classes ?? new Class[0]
                                        select @class)
                                   where @class.Methods.Any(m => m.ShouldSerializeSkippedDueTo())
                                   where @class.Methods.All(m => m.FileRef == null)
                                   from method in @class.Methods.Where(x => !x.ShouldSerializeSkippedDueTo())
                                   select method)
            {
                method.MarkAsSkipped(SkippedMethod.Inferred);
            }
            
            foreach (var module in CoverageSession.Modules.Where(x => !x.ShouldSerializeSkippedDueTo()))
            {

                #region Module File/FileID Dictionary

                sourceRepository = new Dictionary<uint, CodeCoverageStringTextSource>();
                var filesDictionary = new Dictionary<string,uint>();
                foreach (var file in (module.Files ?? new File[0]).Where(file => !String.IsNullOrWhiteSpace(file.FullPath) && !filesDictionary.ContainsKey(file.FullPath)))
                {
                    var source = CodeCoverageStringTextSource.GetSource(file.FullPath);
                    if (source != null) sourceRepository.Add (file.UniqueId, source);
                    filesDictionary.Add(file.FullPath, file.UniqueId);
                }

                #endregion

                #region TODO:? Merge Compiler Extracted/Generated Methods (enumerator methods)

                // Store repeated Query
                var classesQuery = (module.Classes ?? new Class[0]).Where(x => !x.ShouldSerializeSkippedDueTo());

                #endregion

                foreach (var @class in classesQuery)
                {

                    foreach (var method in (@class.Methods ?? new Method[0]).Where(x => !x.ShouldSerializeSkippedDueTo()))
                    {
                        if (method.SequencePoints == null) method.SequencePoints = emptySeqPoints;
                        if (method.BranchPoints == null) method.BranchPoints = emptyBranchPoints;

                        // No sequences in method, but branches present? => remove branches
                        if (method.SequencePoints.Length == 0 && method.BranchPoints.Length != 0) {
                            method.BranchPoints = emptyBranchPoints;
                        }

                        if (method.SequencePoints.Length != 0) MapFileReferences(method.SequencePoints, filesDictionary);
                        if (method.BranchPoints.Length != 0) MapFileReferences(method.BranchPoints, filesDictionary);

                        #region Merge branch-exits

                        // anything to join, filter, merge?
                        if (method.SequencePoints.Length != 0 && method.BranchPoints.Length != 0) {

                            #region Join Sequences and Branches
                            // Quick match branches to sequence using SP&BP sort order by IL offset

                            // SP & BP are sorted by offset and code below expect both SP & BP to be sorted by offset
                            // ATTN: Sorted again to prevent future bugs if order of SP & BP is changed!
                            method.SequencePoints = method.SequencePoints.OrderBy( sp => sp.Offset ).ToArray();
                            method.BranchPoints = method.BranchPoints.OrderBy( bp => bp.Offset ).ToArray();
    
                            // Use stack because Stack.Pop is constant time
                            var branchStack = new Stack<BranchPoint>(method.BranchPoints);

                            // Join offset matching BranchPoints with SequencePoint "parent"
                            // Exclude all branches where BranchPoint.Offset < first method.SequencePoints.Offset
                            // Reverse() starts loop from highest offset to lowest
                            foreach (SequencePoint spParent in method.SequencePoints.Reverse()) {
                            	// create branchPoints "child" list
                                spParent.BranchPoints = new List<BranchPoint>();
                                // if BranchPoint.Offset is >= SequencePoint.Offset 
                                // then move BranchPoint from stack to "child" list (Pop/Add)
                                while (branchStack.Count != 0 && branchStack.Peek().Offset >= spParent.Offset) {
                                    spParent.BranchPoints.Add(branchStack.Pop());
                                }
                            }

                            // clear the stack
                            branchStack.Clear();

                            #endregion

                            #region Remove Compiler Generated Branches from SequencePoints

                            foreach (var sp in method.SequencePoints) {
                                if (sp != null && sp.BranchPoints != null && sp.BranchPoints.Count != 0 && doRemoveBranches(sp)) {
                                    sp.BranchPoints = emptyBranchList;
                                }
                            }

                            #endregion
                            
                            #region Merge Branch-Exits for each Sequence

                            // Collection of validBranchPoints (child/connected to parent SequencePoint)
                            var validBranchPoints = new List<BranchPoint>();

                            var branchExits = new Dictionary<int, BranchPoint>();
                            foreach (var sp in method.SequencePoints) {
                
                                // SequencePoint has branches attached?
                                if (sp.BranchPoints != null && sp.BranchPoints.Count != 0) {
                
                                    // Merge sp.BranchPoints using EndOffset as branchExits key
                                    branchExits.Clear();
                                    foreach (var branchPoint in sp.BranchPoints) {
                                        if (!branchExits.ContainsKey(branchPoint.EndOffset)) {
                                            branchExits[branchPoint.EndOffset] = branchPoint; // insert branch
                                        } else {
                                            branchExits[branchPoint.EndOffset].VisitCount += branchPoint.VisitCount; // update branch
                                        }
                                    }
                
                                    // Update SequencePoint counters
                                    sp.BranchExitsCount = 0;
                                    sp.BranchExitsVisit = 0;
                                    foreach (var branchPoint in branchExits.Values) {
                                        sp.BranchExitsCount += 1;
                                        sp.BranchExitsVisit += branchPoint.VisitCount == 0 ? 0 : 1;
                                    }

                                    // Add to validBranchPoints
                                    validBranchPoints.AddRange(sp.BranchPoints);
                                    sp.BranchPoints = emptyBranchList; // clear
                                }
                            }

                            // Replace original method branchPoints with valid (filtered and joined) branches.
                            // Order is Required by FilePersistanceTest because it does not sets .Offset.
                            // (Order by UniqueSequencePoint is equal to order by .Offset when .Offset is set)
                            method.BranchPoints = validBranchPoints.OrderBy(bp => bp.UniqueSequencePoint).ToArray();
                            validBranchPoints = emptyBranchList; // clear

                            #endregion

                        }

                        #endregion

                        if (method.MethodPoint != null)
                        {
                            method.Visited = (method.MethodPoint.VisitCount > 0);
                        }

                        method.Summary.NumBranchPoints = method.BranchPoints.Count();
                        method.Summary.VisitedBranchPoints = method.BranchPoints.Count(pt => pt.VisitCount != 0);
                        method.Summary.NumSequencePoints = method.SequencePoints.Count();
                        method.Summary.VisitedSequencePoints = method.SequencePoints.Count(pt => pt.VisitCount != 0);

                        if (method.Summary.NumSequencePoints > 0)
                            method.Summary.NumBranchPoints += 1;

                        if (method.Summary.VisitedSequencePoints > 0)
                            method.Summary.VisitedBranchPoints += 1;

                        if (method.FileRef != null)
                        {
                            method.Summary.NumMethods = 1;
                            method.Summary.VisitedMethods = (method.Visited) ? 1 : 0;
                        }

                        AddPoints(@class.Summary, method.Summary);
                        CalculateCoverage(method.Summary);

                        method.SequenceCoverage = method.Summary.SequenceCoverage;
                        method.BranchCoverage = method.Summary.BranchCoverage;

                        method.Summary.MinCyclomaticComplexity = method.Summary.MaxCyclomaticComplexity = Math.Max(1, method.CyclomaticComplexity);

                        if (@class.Summary.MinCyclomaticComplexity == 0)
                            @class.Summary.MinCyclomaticComplexity = method.Summary.MinCyclomaticComplexity;
                        
                        @class.Summary.MinCyclomaticComplexity = Math.Min(@class.Summary.MinCyclomaticComplexity, method.CyclomaticComplexity);
                        @class.Summary.MaxCyclomaticComplexity = Math.Max(@class.Summary.MaxCyclomaticComplexity, method.CyclomaticComplexity);
                    }

                    @class.Summary.NumClasses = (@class.Summary.NumMethods > 0) ? 1 : 0;
                    @class.Summary.VisitedClasses = (@class.Summary.VisitedMethods > 0) ? 1 : 0;

                    AddPoints(module.Summary, @class.Summary);
                    CalculateCoverage(@class.Summary);

                    if (module.Summary.MinCyclomaticComplexity == 0)
                        module.Summary.MinCyclomaticComplexity = @class.Summary.MinCyclomaticComplexity;

                    module.Summary.MinCyclomaticComplexity = Math.Min(module.Summary.MinCyclomaticComplexity, @class.Summary.MinCyclomaticComplexity);
                    module.Summary.MaxCyclomaticComplexity = Math.Max(module.Summary.MaxCyclomaticComplexity, @class.Summary.MaxCyclomaticComplexity);
                }

                AddPoints(CoverageSession.Summary, module.Summary);
                CalculateCoverage(module.Summary);

                if (CoverageSession.Summary.MinCyclomaticComplexity == 0)
                    CoverageSession.Summary.MinCyclomaticComplexity = module.Summary.MinCyclomaticComplexity;

                CoverageSession.Summary.MinCyclomaticComplexity = Math.Min(CoverageSession.Summary.MinCyclomaticComplexity, module.Summary.MinCyclomaticComplexity);
                CoverageSession.Summary.MaxCyclomaticComplexity = Math.Max(CoverageSession.Summary.MaxCyclomaticComplexity, module.Summary.MaxCyclomaticComplexity);
            }

            CalculateCoverage(CoverageSession.Summary);
        }

        private static void MapFileReferences(IEnumerable<IDocumentReference> points, IDictionary<string, uint> filesDictionary)
        {
            foreach (var pt in points.Where(p => p.FileId == 0))
            {
                uint fileid;
                filesDictionary.TryGetValue(pt.Document ?? "", out fileid);
                pt.FileId = fileid;
                // clear document if FileId is found
                pt.Document = pt.FileId != 0 ? null : pt.Document;
            }
        }

        private static void CalculateCoverage(Summary summary)
        {
            if (summary.NumSequencePoints > 0)
                summary.SequenceCoverage = Math.Round((summary.VisitedSequencePoints*100m)/summary.NumSequencePoints, 2);

            if (summary.NumBranchPoints > 0)
                summary.BranchCoverage = Math.Round((summary.VisitedBranchPoints*100m)/summary.NumBranchPoints, 2);
        }

        private static void AddPoints(Summary parent, Summary child)
        {
            parent.NumBranchPoints += child.NumBranchPoints;
            parent.VisitedBranchPoints += child.VisitedBranchPoints;
            parent.NumSequencePoints += child.NumSequencePoints;
            parent.VisitedSequencePoints += child.VisitedSequencePoints;

            parent.NumClasses += child.NumClasses;
            parent.VisitedClasses += child.VisitedClasses;
            parent.NumMethods += child.NumMethods;
            parent.VisitedMethods += child.VisitedMethods;
        }

        /// <summary>
        /// Get the sequence points for a function
        /// </summary>
        /// <param name="modulePath"></param>
        /// <param name="functionToken"></param>
        /// <param name="sequencePoints"></param>
        /// <returns></returns>
        public bool GetSequencePointsForFunction(string modulePath, int functionToken, out InstrumentationPoint[] sequencePoints)
        {
            sequencePoints = new InstrumentationPoint[0];
            Class @class;
            var method = GetMethod(modulePath, functionToken, out @class);
            if (method !=null && method.SequencePoints != null)
            {
                System.Diagnostics.Debug.WriteLine("Getting Sequence points for {0}({1})", method.Name, method.MetadataToken);
                var points = new List<InstrumentationPoint>();
                if (!(method.MethodPoint is SequencePoint))
                    points.Add(method.MethodPoint);
                points.AddRange(method.SequencePoints);
                sequencePoints = points.ToArray();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get the branch ponts for a function
        /// </summary>
        /// <param name="modulePath"></param>
        /// <param name="functionToken"></param>
        /// <param name="branchPoints"></param>
        /// <returns></returns>
        public bool GetBranchPointsForFunction(string modulePath, int functionToken, out BranchPoint[] branchPoints)
        {
            branchPoints = new BranchPoint[0];
            Class @class;
            var method = GetMethod(modulePath, functionToken, out @class);
            if (method != null && method.BranchPoints != null)
            {
                System.Diagnostics.Debug.WriteLine("Getting Branch points for {0}({1})", method.Name, method.MetadataToken);
                branchPoints = method.BranchPoints.ToArray();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get <see cref="Method"/> data for a function
        /// </summary>
        /// <param name="modulePath"></param>
        /// <param name="functionToken"></param>
        /// <param name="class"></param>
        /// <returns></returns>
        private Method GetMethod(string modulePath, int functionToken, out Class @class)
        {
            @class = null;
            //c = null;
            var module = CoverageSession.Modules.FirstOrDefault(x => x.Aliases.Any(path => path.Equals(modulePath, StringComparison.InvariantCultureIgnoreCase)));
            if (module == null)
                return null;
            if (!_moduleMethodMap[module].ContainsKey(functionToken)) return null;
            var pair = _moduleMethodMap[module][functionToken];
            @class = pair.Key;
            return pair.Value;
        }

        /// <summary>
        /// Get the class name of a function
        /// </summary>
        /// <param name="modulePath"></param>
        /// <param name="functionToken"></param>
        /// <returns></returns>
        public string GetClassFullName(string modulePath, int functionToken)
        {
            Class @class;
            GetMethod(modulePath, functionToken, out @class);
            return @class != null ? @class.FullName : null;
        }

        /// <summary>
        /// Save the visit data to the session model
        /// </summary>
        /// <param name="data"></param>
        public void SaveVisitData(byte[] data)
        {
            var nCount = BitConverter.ToUInt32(data, 0);
            if (nCount > (data.Count()/4) - 1)
            {
                _logger.ErrorFormat("Failed to process points as count ({0}) exceeded available buffer size ({1})",
                    nCount, (data.Count()/4) - 1);
                return;
            }
            for (int i = 0, idx = 4; i < nCount; i++, idx += 4)
            {
                var spid = BitConverter.ToUInt32(data, idx);
                if (spid < (uint)MSG_IdType.IT_MethodEnter)
                {
                    if (!InstrumentationPoint.AddVisitCount(spid, _trackedMethodId))
                    {
                        _logger.ErrorFormat("Failed to add a visit to {0} with tracking method {1}. Max point count is {2}",
                            spid, _trackedMethodId, InstrumentationPoint.Count);
                    }
                }
                else
                {
                    var tmId = spid & (uint)MSG_IdType.IT_Mask;
                    _trackedMethodId = (spid & (uint)MSG_IdType.IT_MethodEnter) != 0 ? tmId : 0;
                }
            }
        }

        /// <summary>
        /// determine if the method (test method) should be tracked
        /// </summary>
        /// <param name="modulePath"></param>
        /// <param name="assemblyName"></param>
        /// <param name="functionToken"></param>
        /// <param name="uniqueId"></param>
        /// <returns></returns>
        public bool GetTrackingMethod(string modulePath, string assemblyName, int functionToken, out uint uniqueId)
        {
            uniqueId = 0;
            foreach (var module in CoverageSession.Modules
                .Where(x => x.TrackedMethods != null)
                .Where(x => x.Aliases.Contains(modulePath)))
            {
                foreach (var trackedMethod in module.TrackedMethods)
                {
                    if (trackedMethod.MetadataToken == functionToken)
                    {
                        uniqueId = trackedMethod.UniqueId;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}