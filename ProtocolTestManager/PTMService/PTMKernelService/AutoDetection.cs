// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Protocols.TestManager.Detector;
using Microsoft.Protocols.TestManager.Kernel;
using Microsoft.Protocols.TestManager.PTMService.Abstractions.Kernel;
using Microsoft.Protocols.TestManager.PTMService.Common.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Protocols.TestManager.PTMService.PTMKernelService
{
    public class AutoDetection : IAutoDetection
    {
        private ReaderWriterLockSlim stepsLocker = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim prerequisitesLocker = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim detectorLocker = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim statusLocker = new ReaderWriterLockSlim();

        private Exception detectedException = null;

        private List<DetectingItem> detectSteps;

        private ITestSuite TestSuite { get; set; }

        private CancellationTokenSource cts = null;

        private IValueDetector valueDetector = null;

        private PrerequisiteView prerequisiteView = null;

        private Task detectTask = null;

        private bool taskCanceled = false;

        private string detectorAssembly = string.Empty;

        private string detectorInstanceTypeName = string.Empty;

        private DetectionStatus detectionStatus = DetectionStatus.NotStart;

        private string latestLogPath = string.Empty;

        private string latestDetectorInstanceId = string.Empty;

        private Dictionary<string, StreamWriter> detectLogs = new Dictionary<string, StreamWriter>();
        private Dictionary<string, int> detectStepIndexes = new Dictionary<string, int>();

        /// <summary>
        /// Delegate of logging.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="style"></param>
        public delegate void DetectLog(string message, LogStyle style);

        /// <summary>
        /// Instance of DetectLog.
        /// </summary>
        public DetectLog DetectLogCallback;

        private AutoDetection(ITestSuite testSuite)
        {
            TestSuite = testSuite;

            InitializeDetector(TestSuite.Id);

            detectSteps = ValueDetector.GetDetectionSteps();

            Prerequisites p = GetPrerequisitsInValueDetectorAssembly();
            prerequisiteView = new PrerequisiteView()
            {
                Summary = p.Summary,
                Title = p.Title,
                Properties = new List<Property>()
            };
            foreach (var i in p.Properties)
            {
                prerequisiteView.Properties.Add(new Property()
                {
                    Name = i.Key,
                    Value = ((i.Value != null) && (i.Value.Count > 0)) ? i.Value[0] : null,
                    Choices = i.Value
                });
            }
        }

        public static AutoDetection Create(ITestSuite testSuite)
        {
            var instance = new AutoDetection(testSuite);

            return instance;
        }

        protected IValueDetector ValueDetector
        {
            get
            {
                detectorLocker.EnterUpgradeableReadLock();
                try
                {
                    if (valueDetector == null)
                    {
                        detectorLocker.EnterWriteLock();
                        try
                        {
                            if (valueDetector == null)
                            {
                                // Create an instance
                                Assembly assembly = Assembly.LoadFrom(detectorAssembly);
                                valueDetector = assembly.CreateInstance(detectorInstanceTypeName) as IValueDetector;
                            }
                        }
                        finally
                        {
                            detectorLocker.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    detectorLocker.ExitUpgradeableReadLock();
                }

                return valueDetector;
            }
        }

        /// <summary>
        /// Loads the auto-detect plug-in from assembly file.
        /// </summary>
        /// <param name="detectorAssembly">File name</param>
        public void Load(string detectorAssembly)
        {
            // Get CustomerInterface
            Type interfaceType = typeof(IValueDetector);

            Assembly assembly = Assembly.LoadFrom(detectorAssembly);

            Type[] types = assembly.GetTypes();

            // Find a class that implement Customer Interface
            foreach (Type type in types)
            {
                if (type.IsClass && interfaceType.IsAssignableFrom(type) == true)
                {
                    detectorInstanceTypeName = type.FullName;
                    break;
                }
            }
        }

        public void InitializeDetector(int testSuiteId)
        {
            var ptfconfig = LoadPtfconfig();

            UtilCallBackFunctions.GetPropertyValue = (string name) =>
            {
                var property = ptfconfig.GetPropertyNodeByName(name);
                if (property != null) return property.Value;
                return null;
            };

            UtilCallBackFunctions.GetPropertiesByFile = (filename) =>
            {
                if (!ptfconfig.FileProperties.ContainsKey(filename))
                    return null;
                return ptfconfig.FileProperties[filename];
            };

            detectorAssembly = TestSuite.GetDetectorAssembly();

            Load(detectorAssembly);
        }

        #region Get/Set Prerequisites

        /// <summary>
        /// Gets the properties required for auto-detection.
        /// </summary>
        /// <returns>Prerequisites object.</returns>
        public PrerequisiteView GetPrerequisites()
        {
            prerequisitesLocker.EnterReadLock();
            try
            {
                return prerequisiteView;
            }
            finally
            {
                prerequisitesLocker.ExitReadLock();
            }
        }

        /// <summary>
        /// Sets the property values required for auto-detection.
        /// </summary>
        /// <returns>Returns true if succeeded, otherwise false.</returns>
        public bool SetPrerequisits(List<Property> prerequisiteProperties)
        {
            Dictionary<string, string> properties = new Dictionary<string, string>();
            foreach (var p in prerequisiteProperties)
            {
                properties.Add(p.Name, p.Value);
            };

            prerequisitesLocker.EnterWriteLock();
            try
            {
                prerequisiteView.Properties = prerequisiteProperties;
            }
            finally
            {
                prerequisitesLocker.ExitWriteLock();
            }

            return SetPrerequisitesInValueDetectorAssembly(properties);
        }

        #endregion

        /// <summary>
        /// Gets a list of the detection steps.
        /// </summary>
        /// <returns>A list of the detection steps.</returns>
        public List<DetectingItem> GetDetectedSteps()
        {
            stepsLocker.EnterReadLock();
            try
            {
                return detectSteps;
            }
            finally
            {
                stepsLocker.ExitReadLock();
            }
        }

        public DetectionOutcome GetDetectionOutcome()
        {
            return new DetectionOutcome(GetDetectionStatus(), detectedException);
        }

        public string GetDetectionLog()
        {
            if(!string.IsNullOrEmpty(latestLogPath) && File.Exists(latestLogPath))
            {
                return File.ReadAllText(latestLogPath);
            }
            return string.Empty;
        }

        #region Detection
        /// <summary>
        /// Reset AutoDetection settings
        /// </summary>
        public void Reset()
        {
            CloseLogger();

            if (valueDetector != null)
            {
                valueDetector.Dispose();
                valueDetector = null;
            }

            if (cts != null)
            {
                cts.Dispose();
            }
            
            UtilCallBackFunctions.WriteLog = (message, newline, style) =>
            {
                if (DetectLogCallback != null) DetectLogCallback(message, style);
            };
            
            stepsLocker.EnterWriteLock();
            try
            {
                detectSteps = ValueDetector.GetDetectionSteps();
            }
            finally
            {
                stepsLocker.ExitWriteLock();
            }
            SetDetectionStatus(DetectionStatus.NotStart);
            taskCanceled = false;
            detectedException = null;
        }

        /// <summary>
        /// Begins the auto-detection.
        /// </summary>
        /// <param name="DetectionEvent">Callback function when the detection finished.</param>
        public void StartDetection(DetectionCallback callback)
        {
            if (GetDetectionStatus() == DetectionStatus.InProgress)
            {
                return;
            }

            // attach detect log callback to update detect step status
            AttachDetectLogCallback();

            // start detection
            StartDetection();
        }

        /// <summary>
        /// Stop the auto-detection
        /// </summary>
        public void StopDetection(Action callback)
        {
            SetDetectStepCurrentStatus(DetectingStatus.Canceling);

            StopDetection();
        }

        #endregion

        /// <summary>
        /// Gets an object represents the detection summary.
        /// </summary>
        /// <returns>An object</returns>
        public List<ResultItemMap> GetDetectionSummary()
        {
            return ValueDetector.GetSUTSummary();
        }

        #region Apply Detection Summary to xml

        /// <summary>
        /// Apply Detection Result
        /// </summary>
        /// <param name="ruleGroupsBySelectedRules">The rule groups by selected rules</param>
        /// <param name="properties">The ptfconfig properties.</param>
        public void ApplyDetectionResult(out IEnumerable<Common.Types.RuleGroup> ruleGroupsBySelectedRules, ref IEnumerable<PropertyGroup> properties)
        {
            ApplyDetectedRules(out ruleGroupsBySelectedRules);
            ApplyDetectedValues(ref properties);
        }

        /// <summary>
        /// Apply the test case selection rules detected by the plug-in.
        /// </summary>
        /// <param name="ruleGroupsBySelectedRules">The rule groups by selected rules.</param>
        private void ApplyDetectedRules(out IEnumerable<Common.Types.RuleGroup> ruleGroupsBySelectedRules)
        {
            var ruleGroups = TestSuite.LoadTestCaseFilter();
            var selectedRules = ValueDetector.GetSelectedRules();
            var tempRuleGroups = new List<Common.Types.RuleGroup>();
            foreach (var ruleGroup in ruleGroups)
            {
                List<Common.Types.Rule> rules = GetSelectedRules(ruleGroup.Name, ruleGroup.Rules.ToList(), selectedRules.Where(i => i.Status == RuleStatus.Selected).ToList());
                if (rules.Count > 0)
                {
                    tempRuleGroups.Add(new Common.Types.RuleGroup
                    {
                        DisplayName = ruleGroup.DisplayName,
                        Name = ruleGroup.Name,
                        Rules = rules.ToArray(),
                    });
                }
            }
            ruleGroupsBySelectedRules = tempRuleGroups.ToArray();
        }

        private List<Common.Types.Rule> GetSelectedRules(string ruleGroupName, List<Common.Types.Rule> rules, List<CaseSelectRule> selectedRules)
        {
            List<Common.Types.Rule> myRules = new List<Common.Types.Rule>();
            foreach (var rule in rules)
            {
                Common.Types.Rule myRule = new Common.Types.Rule()
                {
                    Name = rule.Name,
                    DisplayName = rule.DisplayName,
                    Categories = rule.Categories,
                    SelectStatus = rule.SelectStatus
                };
                string ruleName = ruleGroupName + '.' + myRule.Name;
                if (rule.Count > 0)
                {
                    // myRule.Rules is not null means it is parent rule and contains sub rules,
                    var selectedRulesList = GetSelectedRules(ruleName, rule.ToList(), selectedRules);
                    myRule.Clear();
                    foreach (var s in selectedRulesList)
                    {
                        myRule.Add(s);
                    }
                    if (selectedRules.Where(i => ruleName.Contains(i.Name) || i.Name.Contains(ruleName)).Count() > 0)
                    {
                        // 1. ruleName is sub rule of selectedRules: ruleName.Contains(i.Name)
                        // e.g. ruleName:Priority.Non-BVT.Negative, i.Name: Priority.Non-BVT
                        // 2. ruleName is parent rule of selectedRules: i.Name.Contains(ruleName)
                        // e.g. ruleName:SMB Dialect (Please select all supported dialects).SMB Dialects, i.Name: SMB Dialect (Please select all supported dialects).SMB Dialects.SMB 202

                        // If rule.Count > myRule.Count means its sub rules contains unselected item(s),
                        // so 'myRule' is partial selected, and set IsSelected to null; otherwise set IsSelected to true.
                        myRule.SelectStatus = rule.Count > myRule.Count ? Common.Types.RuleSelectStatus.Partial : Common.Types.RuleSelectStatus.Selected;
                        myRules.Add(myRule);
                    }
                }
                else
                {
                    // myRule.Rules is null means it has no sub rules, if ruleName's parent rule is in selectedRules then set IsSelected to true.
                    // e.g. ruleName:Priority.Non-BVT.Positive,i.Name: Priority.Non-BVT
                    if (selectedRules.Where(i => ruleName.Contains(i.Name)).Count() > 0)
                    {
                        myRule.SelectStatus = Common.Types.RuleSelectStatus.Selected;
                        myRules.Add(myRule);
                    }
                }
            }
            return myRules;
        }

        private void ApplyDetectedValues(ref IEnumerable<PropertyGroup> properties)
        {
            Dictionary<string, List<string>> propertiesByDetector;
            ValueDetector.GetDetectedProperty(out propertiesByDetector);
            List<PropertyGroup> updatedPropertyGroupList = new List<PropertyGroup>();
            foreach (var ptfconfigProperty in properties)
            {
                PropertyGroup newPropertyGroup = new PropertyGroup()
                {
                    Name = ptfconfigProperty.Name,
                    Items = ptfconfigProperty.Items,
                };

                foreach (var item in ptfconfigProperty.Items)
                {
                    var propertyFromDetctor = propertiesByDetector.Where(i => i.Key == item.Key);
                    if (propertyFromDetctor.Count() > 0)
                    {
                        var detectorPropertyValue = propertyFromDetctor.FirstOrDefault().Value;
                        var newProperty = newPropertyGroup.Items.Where(i => i.Key == item.Key).FirstOrDefault();
                        if (detectorPropertyValue.Count() == 1)
                        {
                            newProperty.Value = detectorPropertyValue[0];
                        }
                        else if (detectorPropertyValue.Count() > 0)
                        {
                            newProperty.Choices = detectorPropertyValue;
                            newProperty.Value = detectorPropertyValue[0];
                        }
                    }
                }

                updatedPropertyGroupList.Add(newPropertyGroup);
            }
            properties = updatedPropertyGroupList.ToArray();
        }

        #endregion

        #region Private Methods
        /// <summary>
        /// Apply the test case selection rules detected by the plug-in.
        /// </summary>

        private PtfConfig LoadPtfconfig()
        {
            try
            {
                var ptfConfigFiles = TestSuite.GetConfigurationFiles().ToList();
                var ptfconfig = new PtfConfig(ptfConfigFiles);

                return ptfconfig;
            }
            catch (Exception e)
            {
                throw new Exception(string.Format(AutoDetectionConsts.LoadPtfconfigError, e.Message));
            }
        }

        /// <summary>
        /// Gets the properties required for auto-detection.
        /// </summary>
        /// <returns>Prerequisites object.</returns>
        private Prerequisites GetPrerequisitsInValueDetectorAssembly()
        {
            return ValueDetector.GetPrerequisites();
        }

        /// <summary>
        /// Sets the values of the properties required for auto-detection.
        /// </summary>
        /// <param name="properties">Name - value map.</param>
        /// <returns>Returns true if provided values are enough, otherwise returns false.</returns>
        private bool SetPrerequisitesInValueDetectorAssembly(Dictionary<string, string> properties)
        {
            return ValueDetector.SetPrerequisiteProperties(properties);
        }

        /// <summary>
        /// Gets the case selection rules suggested by the detector.
        /// </summary>
        /// <returns>A list of the rules</returns>
        private List<CaseSelectRule> GetRulesInValueDetectorAssembly()
        {
            return ValueDetector.GetSelectedRules();
        }

        /// <summary>
        /// Gets a list of properties to hide.
        /// </summary>
        /// <param name="rules">Test case selection rules</param>
        /// <returns>A list of properties to hide.</returns>
        private List<string> GetHiddenPropertiesInValueDetectorAssembly(List<CaseSelectRule> rules)
        {
            return ValueDetector.GetHiddenProperties(rules);
        }

        private void AttachDetectLogCallback()
        {
            DetectLogCallback = (msg, style) =>
            {
                if (LogWriter != null)
                {
                    LogWriter.WriteLine("[{0}] {1}", DateTime.Now.ToString(), msg);
                    LogWriter.Flush();
                }
            };
        }

        private void StartDetection()
        {
            cts = new CancellationTokenSource();
            var token = cts.Token;

            token.Register(() =>
            {
                taskCanceled = true;
            });

            DetectContext context = new DetectContext((instanceId, stepId, logStyle) =>
             {
                 if (taskCanceled || !instanceId.Equals(latestDetectorInstanceId))
                 {
                     return;
                 }

                 var status = logStyle switch
                 {
                     LogStyle.Default => DetectingStatus.Detecting,
                     LogStyle.Error => DetectingStatus.Error,
                     LogStyle.StepFailed => DetectingStatus.Failed,
                     LogStyle.StepSkipped => DetectingStatus.Skipped,
                     LogStyle.StepNotFound => DetectingStatus.NotFound,
                     LogStyle.StepPassed => DetectingStatus.Finished,
                     _ => DetectingStatus.Finished,
                 };

                 StepIndex = stepId;
                 SetDetectStepCurrentStatus(status);
             }, token);
            latestDetectorInstanceId = context.Id;

            detectTask = new Task(() =>
            {
                token.ThrowIfCancellationRequested();

                // mark detection status as InProgress
                SetDetectionStatus(DetectionStatus.InProgress);
                try
                {
                    var resultStatus = ValueDetector.RunDetection(context) ? DetectionStatus.Finished : DetectionStatus.Error;
                    SetDetectionStatus(resultStatus);
                    detectedException = null;
                }
                catch (Exception ex)
                {
                    SetDetectionStatus(DetectionStatus.Error);
                    detectedException = ex;
                    StopDetection();
                }

                if (StepIndex < GetDetectedSteps().Count)
                {
                    SetDetectStepCurrentStatus(DetectingStatus.Failed);
                }

                CloseLogger();
            }, token);

            latestLogPath = Path.Combine(TestSuite.StorageRoot.AbsolutePath, "Detector_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".log");
            LogWriter = new StreamWriter(latestLogPath);
            detectTask.Start();
        }

        private void StopDetection()
        {
            if (detectTask != null)
            {
                cts.Cancel();
                while (!taskCanceled)
                {
                    Thread.SpinWait(100);
                }
            }
        }

        private void SetDetectionStatus(DetectionStatus status)
        {
            statusLocker.EnterWriteLock();
            try
            {
                detectionStatus = status;
            }
            finally
            {
                statusLocker.ExitWriteLock();
            }
        }

        private DetectionStatus GetDetectionStatus()
        {
            statusLocker.EnterReadLock();
            try
            {
                return detectionStatus;
            }
            finally
            {
                statusLocker.ExitReadLock();
            }
        }

        private void CloseLogger()
        {
            DetectLogCallback = null;

            if (LogWriter != null)
            {
                LogWriter.Close();
                LogWriter.Dispose();
            }

            if (detectLogs.ContainsKey(latestDetectorInstanceId))
            {
                detectLogs.Remove(latestDetectorInstanceId);
            }
        }

        private ReaderWriterLockSlim stepIndexLocker = new ReaderWriterLockSlim();
        private int StepIndex
        {
            get
            {
                stepIndexLocker.EnterReadLock();
                try
                {
                    if (detectStepIndexes.ContainsKey(latestDetectorInstanceId))
                    {
                        return detectStepIndexes[latestDetectorInstanceId];
                    }
                    else
                    {
                        return -1;
                    }
                }
                finally
                {
                    stepIndexLocker.ExitReadLock();
                }
            }
            set
            {
                stepIndexLocker.EnterWriteLock();
                try
                {
                    if (detectStepIndexes.ContainsKey(latestDetectorInstanceId))
                    {
                        detectStepIndexes[latestDetectorInstanceId] = value;
                    }
                    else
                    {
                        detectStepIndexes.Add(latestDetectorInstanceId, 0);
                    }
                }
                finally
                {
                    stepIndexLocker.ExitWriteLock();
                }
            }
        }

        private ReaderWriterLockSlim logLocker = new ReaderWriterLockSlim();
        private StreamWriter LogWriter
        {
            get
            {
                logLocker.EnterReadLock();
                try
                {
                    if (detectLogs.ContainsKey(latestDetectorInstanceId))
                    {
                        return detectLogs[latestDetectorInstanceId];
                    }

                    return null;
                }
                finally
                {
                    logLocker.ExitReadLock();
                }
            }
            set
            {
                logLocker.EnterWriteLock();
                try
                {
                    if (detectLogs.ContainsKey(latestDetectorInstanceId))
                    {
                        detectLogs[latestDetectorInstanceId] = value;
                    }
                    else
                    {
                        detectLogs.Add(latestDetectorInstanceId, value);
                    }
                }
                finally
                {
                    logLocker.ExitWriteLock();
                }
            }
        }

        private void SetDetectStepCurrentStatus(DetectingStatus detectingStatus)
        {
            if (StepIndex >= detectSteps.Count)
                return;

            stepsLocker.EnterWriteLock();
            try
            {
                detectSteps[StepIndex].DetectingStatus = detectingStatus;
            }
            finally
            {
                stepsLocker.ExitWriteLock();
            }
        }
        #endregion
    }
}