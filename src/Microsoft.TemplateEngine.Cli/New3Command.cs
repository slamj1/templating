// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Mount;
using Microsoft.TemplateEngine.Cli.CommandParsing;
using Microsoft.TemplateEngine.Cli.PostActionProcessors;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.TemplateEngine.Utils;

namespace Microsoft.TemplateEngine.Cli
{
    public class New3Command
    {
        private readonly ITelemetryLogger _telemetryLogger;
        private readonly TemplateCreator _templateCreator;
        private readonly SettingsLoader _settingsLoader;
        private readonly AliasRegistry _aliasRegistry;
        private readonly Paths _paths;
        private readonly INewCommandInput _commandInput;
        private readonly IHostSpecificDataLoader _hostDataLoader;
        private readonly string _defaultLanguage;
        private static readonly Regex LocaleFormatRegex = new Regex(@"
                    ^
                        [a-z]{2}
                        (?:-[A-Z]{2})?
                    $"
            , RegexOptions.IgnorePatternWhitespace);
        private readonly Action<IEngineEnvironmentSettings, IInstaller> _onFirstRun;

        public New3Command(string commandName, ITemplateEngineHost host, ITelemetryLogger telemetryLogger, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, INewCommandInput commandInput)
            : this(commandName, host, telemetryLogger, onFirstRun, commandInput, null)
        {
        }

        public New3Command(string commandName, ITemplateEngineHost host, ITelemetryLogger telemetryLogger, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, INewCommandInput commandInput, string hivePath)
        {
            _telemetryLogger = telemetryLogger;
            host = new ExtendedTemplateEngineHost(host, this);
            EnvironmentSettings = new EngineEnvironmentSettings(host, x => new SettingsLoader(x), hivePath);
            _settingsLoader = (SettingsLoader)EnvironmentSettings.SettingsLoader;
            Installer = new Installer(EnvironmentSettings);
            _templateCreator = new TemplateCreator(EnvironmentSettings);
            _aliasRegistry = new AliasRegistry(EnvironmentSettings);
            CommandName = commandName;
            _paths = new Paths(EnvironmentSettings);
            _onFirstRun = onFirstRun;
            _hostDataLoader = new HostSpecificDataLoader(EnvironmentSettings.SettingsLoader);
            _commandInput = commandInput;

            if (!EnvironmentSettings.Host.TryGetHostParamDefault("prefs:language", out _defaultLanguage))
            {
                _defaultLanguage = null;
            }
        }

        public static IInstaller Installer { get; set; }

        public string CommandName { get; }

        public string TemplateName => _commandInput.TemplateName;

        public string OutputPath => _commandInput.OutputPath;

        public EngineEnvironmentSettings EnvironmentSettings { get; private set; }

        public static int Run(string commandName, ITemplateEngineHost host, ITelemetryLogger telemetryLogger, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, string[] args)
        {
            return Run(commandName, host, telemetryLogger, onFirstRun, args, null);
        }

        public static int Run(string commandName, ITemplateEngineHost host, ITelemetryLogger telemetryLogger, Action<IEngineEnvironmentSettings, IInstaller> onFirstRun, string[] args, string hivePath)
        {
            if (args.Any(x => string.Equals(x, "--debug:attach", StringComparison.Ordinal)))
            {
                Console.ReadLine();
            }

            if (args.Length == 0)
            {
                telemetryLogger.TrackEvent(commandName + "-CalledWithNoArgs");
            }

            INewCommandInput commandInput = new NewCommandInputCli(commandName);
            New3Command instance = new New3Command(commandName, host, telemetryLogger, onFirstRun, commandInput, hivePath);

            commandInput.OnExecute(instance.ExecuteAsync);

            int result;
            try
            {
                using (Timing.Over(host, "Execute"))
                {
                    result = commandInput.Execute(args);
                }
            }
            catch (Exception ex)
            {
                AggregateException ax = ex as AggregateException;

                while (ax != null && ax.InnerExceptions.Count == 1)
                {
                    ex = ax.InnerException;
                    ax = ex as AggregateException;
                }

                Reporter.Error.WriteLine(ex.Message.Bold().Red());

                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    ax = ex as AggregateException;

                    while (ax != null && ax.InnerExceptions.Count == 1)
                    {
                        ex = ax.InnerException;
                        ax = ex as AggregateException;
                    }

                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                }

                Reporter.Error.WriteLine(ex.StackTrace.Bold().Red());
                result = 1;
            }

            return result;
        }

        private void ConfigureEnvironment()
        {
            _onFirstRun?.Invoke(EnvironmentSettings, Installer);

            foreach (Type type in typeof(New3Command).GetTypeInfo().Assembly.GetTypes())
            {
                EnvironmentSettings.SettingsLoader.Components.Register(type);
            }
        }

        private async Task<CreationResultStatus> CreateTemplateAsync(ITemplateInfo template)
        {
            string fallbackName = new DirectoryInfo(_commandInput.OutputPath ?? Directory.GetCurrentDirectory()).Name;

            if (string.IsNullOrEmpty(fallbackName) || string.Equals(fallbackName, "/", StringComparison.Ordinal))
            {   // DirectoryInfo("/").Name on *nix returns "/", as opposed to null or "".
                fallbackName = null;
            }

            TemplateCreationResult instantiateResult;

            try
            {
                instantiateResult = await _templateCreator.InstantiateAsync(template, _commandInput.Name, fallbackName, _commandInput.OutputPath, _commandInput.AllTemplateParams, _commandInput.SkipUpdateCheck, _commandInput.IsForceFlagSpecified, _commandInput.BaselineName).ConfigureAwait(false);
            }
            catch (ContentGenerationException cx)
            {
                Reporter.Error.WriteLine(cx.Message.Bold().Red());
                if(cx.InnerException != null)
                {
                    Reporter.Error.WriteLine(cx.InnerException.Message.Bold().Red());
                }

                return CreationResultStatus.CreateFailed;
            }
            catch (TemplateAuthoringException tae)
            {
                Reporter.Error.WriteLine(tae.Message.Bold().Red());
                return CreationResultStatus.CreateFailed;
            }

            string resultTemplateName = string.IsNullOrEmpty(instantiateResult.TemplateFullName) ? TemplateName : instantiateResult.TemplateFullName;

            switch (instantiateResult.Status)
            {
                case CreationResultStatus.Success:
                    Reporter.Output.WriteLine(string.Format(LocalizableStrings.CreateSuccessful, resultTemplateName));

                    if(!string.IsNullOrEmpty(template.ThirdPartyNotices))
                    {
                        Reporter.Output.WriteLine(string.Format(LocalizableStrings.ThirdPartyNotices, template.ThirdPartyNotices));
                    }

                    HandlePostActions(instantiateResult);
                    break;
                case CreationResultStatus.CreateFailed:
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.CreateFailed, resultTemplateName, instantiateResult.Message).Bold().Red());
                    break;
                case CreationResultStatus.MissingMandatoryParam:
                    if (string.Equals(instantiateResult.Message, "--name", StringComparison.Ordinal))
                    {
                        Reporter.Error.WriteLine(string.Format(LocalizableStrings.MissingRequiredParameter, instantiateResult.Message, resultTemplateName).Bold().Red());
                    }
                    else
                    {
                        IReadOnlyList<string> missingParamNamesCanonical = instantiateResult.Message.Split(new[] { ',' })
                            .Select(x => _commandInput.VariantsForCanonical(x.Trim())
                                                        .DefaultIfEmpty(x.Trim()).First())
                            .ToList();
                        string fixedMessage = string.Join(", ", missingParamNamesCanonical);
                        Reporter.Error.WriteLine(string.Format(LocalizableStrings.MissingRequiredParameter, fixedMessage, resultTemplateName).Bold().Red());
                    }
                    break;
                case CreationResultStatus.OperationNotSpecified:
                    break;
                case CreationResultStatus.InvalidParamValues:
                    IReadOnlyList<InvalidParameterInfo> invalidParameterList = GetTemplateUsageInformation(template, out IParameterSet ps, out IReadOnlyList<string> userParamsWithInvalidValues, out IReadOnlyDictionary<string, IReadOnlyList<string>> variantsForCanonicals, out HashSet<string> userParamsWithDefaultValues, out bool hasPostActionScriptRunner);
                    string invalidParamsError = InvalidParameterInfo.InvalidParameterListToString(invalidParameterList);
                    Reporter.Error.WriteLine(invalidParamsError.Bold().Red());
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, $"{CommandName} {TemplateName}").Bold().Red());
                    break;
                default:
                    break;
            }

            return instantiateResult.Status;
        }

        private IReadOnlyList<InvalidParameterInfo> GetTemplateUsageInformation(ITemplateInfo templateInfo, out IParameterSet allParams, out IReadOnlyList<string> userParamsWithInvalidValues,
                                                    out IReadOnlyDictionary<string, IReadOnlyList<string>> variantsForCanonicals, out HashSet<string> userParamsWithDefaultValues, out bool hasPostActionScriptRunner)
        {
            ITemplate template = EnvironmentSettings.SettingsLoader.LoadTemplate(templateInfo, _commandInput.BaselineName);
            ParseTemplateArgs(templateInfo);
            allParams = _templateCreator.SetupDefaultParamValuesFromTemplateAndHost(template, template.DefaultName ?? "testName", out IReadOnlyList<string> defaultParamsWithInvalidValues);
            _templateCreator.ResolveUserParameters(template, allParams, _commandInput.AllTemplateParams, out userParamsWithInvalidValues);
            hasPostActionScriptRunner = CheckIfTemplateHasScriptRunningPostActions(template);
            _templateCreator.ReleaseMountPoints(template);

            List<InvalidParameterInfo> invalidParameters = new List<InvalidParameterInfo>();

            if (userParamsWithInvalidValues.Any())
            {
                // Lookup the input param formats - userParamsWithInvalidValues has canonical.
                foreach (string canonical in userParamsWithInvalidValues)
                {
                    _commandInput.AllTemplateParams.TryGetValue(canonical, out string specifiedValue);
                    string inputFormat = _commandInput.TemplateParamInputFormat(canonical);
                    InvalidParameterInfo invalidParam = new InvalidParameterInfo(inputFormat, specifiedValue, canonical);
                    invalidParameters.Add(invalidParam);
                }
            }

            if (_templateCreator.AnyParametersWithInvalidDefaultsUnresolved(defaultParamsWithInvalidValues, userParamsWithInvalidValues, _commandInput.AllTemplateParams, out IReadOnlyList<string> defaultsWithUnresolvedInvalidValues))
            {
                IParameterSet templateParams = template.Generator.GetParametersForTemplate(EnvironmentSettings, template);

                foreach (string defaultParamName in defaultsWithUnresolvedInvalidValues)
                {
                    ITemplateParameter param = templateParams.ParameterDefinitions.FirstOrDefault(x => string.Equals(x.Name, defaultParamName, StringComparison.Ordinal));

                    if (param != null)
                    {
                        // Get the best input format available.
                        IReadOnlyList<string> inputVariants = _commandInput.VariantsForCanonical(param.Name);
                        string displayName = inputVariants.FirstOrDefault(x => x.Contains(param.Name))
                            ?? inputVariants.Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur)
                            ?? param.Name;

                        InvalidParameterInfo invalidParam = new InvalidParameterInfo(displayName, param.DefaultValue, displayName, true);
                        invalidParameters.Add(invalidParam);
                    }
                }
            }

            // get all the flags
            // get all the user input params that have the default value
            Dictionary<string, IReadOnlyList<string>> inputFlagVariants = new Dictionary<string, IReadOnlyList<string>>();
            userParamsWithDefaultValues = new HashSet<string>();
            foreach (string paramName in allParams.ParameterDefinitions.Select(x => x.Name))
            {
                inputFlagVariants[paramName] = _commandInput.VariantsForCanonical(paramName);

                if (_commandInput.TemplateParamHasValue(paramName) && string.IsNullOrEmpty(_commandInput.TemplateParamValue(paramName)))
                {
                    userParamsWithDefaultValues.Add(paramName);
                }
            }
            variantsForCanonicals = inputFlagVariants;

            return invalidParameters;
        }

        private bool CheckIfTemplateHasScriptRunningPostActions(ITemplate template)
        {
            // use a throwaway set of params for getting the creation effects - it makes changes to them.
            string targetDir = _commandInput.OutputPath ?? EnvironmentSettings.Host.FileSystem.GetCurrentDirectory();
            IParameterSet paramsForCreationEffects = _templateCreator.SetupDefaultParamValuesFromTemplateAndHost(template, template.DefaultName ?? "testName", out IReadOnlyList<string> throwaway);
            _templateCreator.ResolveUserParameters(template, paramsForCreationEffects, _commandInput.AllTemplateParams, out IReadOnlyList<string> userParamsWithInvalidValues);
            ICreationEffects creationEffects = template.Generator.GetCreationEffects(EnvironmentSettings, template, paramsForCreationEffects, EnvironmentSettings.SettingsLoader.Components, targetDir);
            return creationEffects.CreationResult.PostActions.Any(x => x.ActionId == ProcessStartPostActionProcessor.ActionProcessorId);
        }

        private string GetLanguageMismatchErrorMessage(string inputLanguage)
        {
            string inputFlagForm;
            if (_commandInput.Tokens.Contains("-lang"))
            {
                inputFlagForm = "-lang";
            }
            else
            {
                inputFlagForm = "--language";
            }

            string invalidLanguageErrorText = LocalizableStrings.InvalidTemplateParameterValues;
            invalidLanguageErrorText += Environment.NewLine + string.Format(LocalizableStrings.InvalidParameterDetail, inputFlagForm, inputLanguage, "language");
            return invalidLanguageErrorText;
        }

        private void HandlePostActions(TemplateCreationResult creationResult)
        {
            if (creationResult.Status != CreationResultStatus.Success)
            {
                return;
            }

            AllowPostActionsSetting scriptRunSettings;

            if (string.IsNullOrEmpty(_commandInput.AllowScriptsToRun) || string.Equals(_commandInput.AllowScriptsToRun, "prompt", StringComparison.OrdinalIgnoreCase))
            {
                scriptRunSettings = AllowPostActionsSetting.Prompt;
            }
            else if (string.Equals(_commandInput.AllowScriptsToRun, "yes", StringComparison.OrdinalIgnoreCase))
            {
                scriptRunSettings = AllowPostActionsSetting.Yes;
            }
            else if (string.Equals(_commandInput.AllowScriptsToRun, "no", StringComparison.OrdinalIgnoreCase))
            {
                scriptRunSettings = AllowPostActionsSetting.No;
            }
            else
            {
                scriptRunSettings = AllowPostActionsSetting.Prompt;
            }

            PostActionDispatcher postActionDispatcher = new PostActionDispatcher(EnvironmentSettings, creationResult, scriptRunSettings);
            postActionDispatcher.Process(() => Console.ReadLine());
        }

        // Checks the result of TemplatesToDisplayInfoAbout()
        // If they all have the same group identity, return them.
        // Otherwise retun an empty list.
        private IEnumerable<ITemplateInfo> GetTemplatesToShowDetailedHelpAbout(TemplateListResolutionResult templateResolutionResult)
        {
            IReadOnlyList<ITemplateInfo> candidateTemplates = GetTemplatesToDisplayInfoAbout(templateResolutionResult);
            Func<ITemplateInfo, string> groupIdentitySelector = (x) => x.GroupIdentity;

            if (candidateTemplates.AllAreTheSame(groupIdentitySelector, StringComparer.OrdinalIgnoreCase))
            {
                return candidateTemplates;
            }

            return new List<ITemplateInfo>();
        }

        // If there are secondary matches, return them
        // Else if there are primary matches, return them
        // Otherwise return all templates in the current context
        private IReadOnlyList<ITemplateInfo> GetTemplatesToDisplayInfoAbout(TemplateListResolutionResult templateResolutionResult)
        {
            IEnumerable<ITemplateInfo> templateList;

            if (templateResolutionResult.HasUnambiguousTemplateGroupToUse)
            {
                templateList = templateResolutionResult.UnambiguousTemplateGroupToUse.Select(x => x.Info);
            }
            else if (!string.IsNullOrEmpty(TemplateName) && templateResolutionResult.HasMatchedTemplatesWithSecondaryMatchInfo)
            {   // without template name, it's not reasonable to do secondary matching
                templateList = templateResolutionResult.MatchedTemplatesWithSecondaryMatchInfo.Select(x => x.Info);
            }
            else if (templateResolutionResult.HasCoreMatchedTemplatesWithDisposition(x => x.IsMatch))
            {
                templateList = templateResolutionResult.CoreMatchedTemplates.Where(x => x.IsMatch).Select(x => x.Info);
            }
            else if (templateResolutionResult.HasCoreMatchedTemplatesWithDisposition(x => x.IsPartialMatch))
            {
                templateList = templateResolutionResult.CoreMatchedTemplates.Where(x => x.IsPartialMatch).Select(x => x.Info);
            }
            else
            {
                templateList = TemplateListResolver.PerformAllTemplatesInContextQuery(_settingsLoader.UserTemplateCache.TemplateInfo, _hostDataLoader, _commandInput.TypeFilter?.ToLowerInvariant())
                                                                    .Where(x => x.IsMatch).Select(x => x.Info);
            }

            return templateList.ToList();
        }

        private void DisplayTemplateList(TemplateListResolutionResult templateResolutionResult)
        {
            IReadOnlyList<ITemplateInfo> results = GetTemplatesToDisplayInfoAbout(templateResolutionResult);
            IEnumerable<IGrouping<string, ITemplateInfo>> grouped = results.GroupBy(x => x.GroupIdentity, x => !string.IsNullOrEmpty(x.GroupIdentity));
            Dictionary<ITemplateInfo, string> templatesVersusLanguages = new Dictionary<ITemplateInfo, string>();

            foreach (IGrouping<string, ITemplateInfo> grouping in grouped)
            {
                List<string> languageForDisplay = new List<string>();
                HashSet<string> uniqueLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string defaultLanguageDisplay = string.Empty;

                foreach (ITemplateInfo info in grouping)
                {
                    if (info.Tags != null && info.Tags.TryGetValue("language", out ICacheTag languageTag))
                    {
                        foreach (string lang in languageTag.ChoicesAndDescriptions.Keys)
                        {
                            if (uniqueLanguages.Add(lang))
                            {
                                if (string.IsNullOrEmpty(_commandInput.Language) && string.Equals(_defaultLanguage, lang, StringComparison.OrdinalIgnoreCase))
                                {
                                    defaultLanguageDisplay = $"[{lang}]";
                                }
                                else
                                {
                                    languageForDisplay.Add(lang);
                                }
                            }
                        }
                    }
                }

                languageForDisplay.Sort(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(defaultLanguageDisplay))
                {
                    languageForDisplay.Insert(0, defaultLanguageDisplay);
                }

                templatesVersusLanguages[grouping.First()] = string.Join(", ", languageForDisplay);
            }

            HelpFormatter<KeyValuePair<ITemplateInfo, string>> formatter = HelpFormatter.For(EnvironmentSettings, templatesVersusLanguages, 6, '-', false)
                .DefineColumn(t => t.Key.Name, LocalizableStrings.Templates)
                .DefineColumn(t => t.Key.ShortName, LocalizableStrings.ShortName)
                .DefineColumn(t => t.Value, out object languageColumn, LocalizableStrings.Language)
                .DefineColumn(t => t.Key.Classifications != null ? string.Join("/", t.Key.Classifications) : null, out object tagsColumn, LocalizableStrings.Tags)
                .OrderByDescending(languageColumn, new NullOrEmptyIsLastStringComparer())
                .OrderBy(tagsColumn);
            Reporter.Output.WriteLine(formatter.Layout());

            if (!_commandInput.IsListFlagSpecified)
            {
                Reporter.Output.WriteLine();
                ShowInvocationExamples(templateResolutionResult);
                IList<ITemplateInfo> templatesToShow = GetTemplatesToShowDetailedHelpAbout(templateResolutionResult).ToList();
                ShowTemplateGroupHelp(templatesToShow);
            }
        }

        private CreationResultStatus EnterAmbiguousTemplateManipulationFlow(TemplateListResolutionResult templateResolutionResult)
        {
            if (!string.IsNullOrEmpty(TemplateName)
                && templateResolutionResult.CoreMatchedTemplates.Count > 0
                && templateResolutionResult.CoreMatchedTemplates.All(x => x.MatchDisposition.Any(d => d.Location == MatchLocation.Language && d.Kind == MatchKind.Mismatch)))
            {
                string errorMessage = GetLanguageMismatchErrorMessage(_commandInput.Language);
                Reporter.Error.WriteLine(errorMessage.Bold().Red());
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, $"{CommandName} {TemplateName}").Bold().Red());
                return CreationResultStatus.NotFound;
            }

            if (!TemplateListResolver.ValidateRemainingParameters(_commandInput, out IReadOnlyList<string> invalidParams) || (!_commandInput.IsListFlagSpecified && !string.IsNullOrEmpty(TemplateName)))
            {
                DisplayInvalidParameters(invalidParams);
                bool anyPartialMatchesDisplayed = ShowTemplateNameMismatchHelp(templateResolutionResult);
                DisplayTemplateList(templateResolutionResult);
                return CreationResultStatus.NotFound;
            }

            if (!string.IsNullOrWhiteSpace(_commandInput.Alias))
            {
                Reporter.Error.WriteLine(LocalizableStrings.InvalidInputSwitch.Bold().Red());
                Reporter.Error.WriteLine("  " + _commandInput.TemplateParamInputFormat("--alias").Bold().Red());
                return CreationResultStatus.NotFound;
            }

            if (_commandInput.IsHelpFlagSpecified)
            {
                _telemetryLogger.TrackEvent(CommandName + "-Help");
                ShowUsageHelp();
                DisplayTemplateList(templateResolutionResult);
                return CreationResultStatus.Success;
            }
            else
            {
                DisplayTemplateList(templateResolutionResult);

                //If we're showing the list because we were asked to, exit with success, otherwise, exit with failure
                if (_commandInput.IsListFlagSpecified)
                {
                    return CreationResultStatus.Success;
                }
                else
                {
                    return CreationResultStatus.OperationNotSpecified;
                }
            }
        }

        private CreationResultStatus EnterInstallFlow()
        {
            _telemetryLogger.TrackEvent(CommandName + "-Install", new Dictionary<string, string> { { "CountOfThingsToInstall", _commandInput.ToInstallList.Count.ToString() } });

            Installer.InstallPackages(_commandInput.ToInstallList);

            //TODO: When an installer that directly calls into NuGet is available,
            //  return a more accurate representation of the outcome of the operation
            return CreationResultStatus.Success;
        }

        private CreationResultStatus EnterMaintenanceFlow()
        {
            if (!TemplateListResolver.ValidateRemainingParameters(_commandInput, out IReadOnlyList<string> invalidParams))
            {
                DisplayInvalidParameters(invalidParams);
                if (_commandInput.IsHelpFlagSpecified)
                {
                    _telemetryLogger.TrackEvent(CommandName + "-Help");
                    ShowUsageHelp();
                }
                else
                {
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, CommandName).Bold().Red());
                }

                return CreationResultStatus.InvalidParamValues;
            }

            if (_commandInput.ToInstallList != null && _commandInput.ToInstallList.Count > 0 && _commandInput.ToInstallList[0] != null)
            {
                Installer.Uninstall(_commandInput.ToInstallList.Select(x => x.Split(new[] { "::" }, StringSplitOptions.None)[0]));
            }

            if (_commandInput.ToUninstallList != null && _commandInput.ToUninstallList.Count > 0 && _commandInput.ToUninstallList[0] != null)
            {
                IEnumerable<string> failures = Installer.Uninstall(_commandInput.ToUninstallList);

                foreach (string failure in failures)
                {
                    Console.WriteLine(LocalizableStrings.CouldntUninstall, failure);
                }
            }

            if (_commandInput.ToInstallList != null && _commandInput.ToInstallList.Count > 0 && _commandInput.ToInstallList[0] != null)
            {
                CreationResultStatus installResult = EnterInstallFlow();

                if (installResult == CreationResultStatus.Success)
                {
                    _settingsLoader.Reload();
                    TemplateListResolutionResult resolutionResult = QueryForTemplateMatches();
                    DisplayTemplateList(resolutionResult);
                }

                return installResult;
            }

            //No other cases specified, we've fallen through to "Usage help + List"
            ShowUsageHelp();
            TemplateListResolutionResult templateResolutionResult = QueryForTemplateMatches();
            DisplayTemplateList(templateResolutionResult);

            return CreationResultStatus.Success;
        }

        private CreationResultStatus SingularGroupDisplayTemplateListIfAnyAreValid(TemplateListResolutionResult templateResolutionResult)
        {
            bool anyValid = false;

            foreach (IFilteredTemplateInfo templateInfo in templateResolutionResult.UnambiguousTemplateGroupToUse)
            {
                ParseTemplateArgs(templateInfo.Info);

                if (!TemplateListResolver.AnyRemainingParameters(_commandInput))
                {
                    anyValid = true;
                    break;
                }
            }

            if (!anyValid)
            {
                IFilteredTemplateInfo highestPrecedenceTemplate = TemplateListResolver.FindHighestPrecedenceTemplateIfAllSameGroupIdentity(templateResolutionResult.UnambiguousTemplateGroupToUse);

                if (highestPrecedenceTemplate != null)
                {
                    ParseTemplateArgs(highestPrecedenceTemplate.Info);
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, CommandName).Bold().Red());
                    return CreationResultStatus.InvalidParamValues;
                }
            }

            DisplayTemplateList(templateResolutionResult);
            return CreationResultStatus.Success;
        }

        private CreationResultStatus DisplayTemplateHelpForSingularGroup(TemplateListResolutionResult templateResolutionResult)
        {
            bool anyArgsErrors = false;
            ShowUsageHelp();
            bool showImplicitlyHiddenParams = templateResolutionResult.UnambiguousTemplateGroupToUse.Count > 1;

            IList<ITemplateInfo> templatesToShowHelpOn = new List<ITemplateInfo>();
            HashSet<string> argsInvalidForAllTemplatesInGroup = new HashSet<string>();
            bool firstTemplate = true;

            foreach (IFilteredTemplateInfo templateInfo in templateResolutionResult.UnambiguousTemplateGroupToUse.OrderByDescending(x => x.Info.Precedence))
            {
                bool argsError = false;
                string commandParseFailureMessage = null;

                try
                {
                    ParseTemplateArgs(templateInfo.Info);
                }
                catch (CommandParserException ex)
                {
                    commandParseFailureMessage = ex.Message;
                    argsError = true;
                }

                if (!argsError)
                {
                    argsError = !TemplateListResolver.ValidateRemainingParameters(_commandInput, out IReadOnlyList<string> invalidParamsForTemplate);
                    if (argsError)
                    {
                        if (firstTemplate)
                        {
                            argsInvalidForAllTemplatesInGroup.UnionWith(invalidParamsForTemplate);
                        }
                        else
                        {
                            argsInvalidForAllTemplatesInGroup.IntersectWith(invalidParamsForTemplate);
                        }
                    }
                }

                if (commandParseFailureMessage != null)
                {
                    Reporter.Error.WriteLine(commandParseFailureMessage.Bold().Red());
                }

                templatesToShowHelpOn.Add(templateInfo.Info);
                anyArgsErrors |= argsError;
                firstTemplate = false;
            }

            if (argsInvalidForAllTemplatesInGroup.Count > 0)
            {
                DisplayInvalidParameters(argsInvalidForAllTemplatesInGroup.ToList());
            }

            ShowTemplateGroupHelp(templatesToShowHelpOn, showImplicitlyHiddenParams);

            return anyArgsErrors ? CreationResultStatus.InvalidParamValues : CreationResultStatus.Success;
        }

        private async Task<CreationResultStatus> EnterSingularTemplateManipulationFlowAsync(TemplateListResolutionResult templateResolutionResult)
        {
            if (_commandInput.IsListFlagSpecified)
            {
                return SingularGroupDisplayTemplateListIfAnyAreValid(templateResolutionResult);
            }
            else if (_commandInput.IsHelpFlagSpecified)
            {
                _telemetryLogger.TrackEvent(CommandName + "-Help");
                return DisplayTemplateHelpForSingularGroup(templateResolutionResult);
            }

            bool argsError = false;
            string commandParseFailureMessage = null;
            IFilteredTemplateInfo highestPrecedenceTemplate = TemplateListResolver.FindHighestPrecedenceTemplateIfAllSameGroupIdentity(templateResolutionResult.UnambiguousTemplateGroupToUse);
            try
            {
                ParseTemplateArgs(highestPrecedenceTemplate.Info);
            }
            catch (CommandParserException ex)
            {
                commandParseFailureMessage = ex.Message;
                argsError = true;
            }

            if (!argsError)
            {
                if (!TemplateListResolver.ValidateRemainingParameters(_commandInput, out IReadOnlyList<string> invalidParams))
                {
                    DisplayInvalidParameters(invalidParams);
                    argsError = true;
                }
                else
                {
                    argsError = false;
                }
            }

            highestPrecedenceTemplate.Info.Tags.TryGetValue("language", out ICacheTag language);
            _commandInput.AllTemplateParams.TryGetValue("framework", out string framework);
            _commandInput.AllTemplateParams.TryGetValue("auth", out string auth);
            bool isMicrosoftAuthored = string.Equals(highestPrecedenceTemplate.Info.Author, "Microsoft", StringComparison.OrdinalIgnoreCase);
            string templateName = isMicrosoftAuthored ? highestPrecedenceTemplate.Info.Identity : "(3rd Party)";

            if (!isMicrosoftAuthored)
            {
                auth = null;
            }

            if (argsError)
            {
                _telemetryLogger.TrackEvent(CommandName + "CreateTemplate", new Dictionary<string, string>
                {
                    { "language", language?.ChoicesAndDescriptions.Keys.FirstOrDefault() },
                    { "argument-error", "true" },
                    { "framework", framework },
                    { "template-name", templateName },
                    { "auth", auth }
                });

                if (commandParseFailureMessage != null)
                {
                    Reporter.Error.WriteLine(commandParseFailureMessage.Bold().Red());
                }

                Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, $"{CommandName} {TemplateName}").Bold().Red());
                return CreationResultStatus.InvalidParamValues;
            }
            else
            {
                bool success = true;

                try
                {
                    return await CreateTemplateAsync(highestPrecedenceTemplate.Info).ConfigureAwait(false);
                }
                catch (ContentGenerationException cx)
                {
                    success = false;
                    Reporter.Error.WriteLine(cx.Message.Bold().Red());
                    if(cx.InnerException != null)
                    {
                        Reporter.Error.WriteLine(cx.InnerException.Message.Bold().Red());
                    }

                    return CreationResultStatus.CreateFailed;
                }
                catch (Exception ex)
                {
                    success = false;
                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                }
                finally
                {
                    _telemetryLogger.TrackEvent(CommandName + "CreateTemplate", new Dictionary<string, string>
                    {
                        { "language", language?.ChoicesAndDescriptions.Keys.FirstOrDefault() },
                        { "argument-error", "false" },
                        { "framework", framework },
                        { "template-name", templateName },
                        { "create-success", success.ToString() },
                        { "auth", auth }
                    });
                }

                return CreationResultStatus.CreateFailed;
            }
        }

        private async Task<CreationResultStatus> EnterTemplateManipulationFlowAsync()
        {
            TemplateListResolutionResult templateResolutionResult = QueryForTemplateMatches();

            if (templateResolutionResult.HasUnambiguousTemplateGroupToUse
                && !templateResolutionResult.UnambiguousTemplateGroupToUse.Any(x => x.HasAmbiguousParameterMatch))
            {
                // unambiguous templates should all have the same dispositions
                if (templateResolutionResult.UnambiguousTemplateGroupToUse[0].MatchDisposition.Any(x => x.Kind == MatchKind.Exact && x.Location != MatchLocation.Context && x.Location != MatchLocation.Language))
                {
                    return await EnterSingularTemplateManipulationFlowAsync(templateResolutionResult).ConfigureAwait(false);
                }
                else if(EnvironmentSettings.Host.OnConfirmPartialMatch(templateResolutionResult.UnambiguousTemplateGroupToUse[0].Info.Name))
                {   // unambiguous templates will all have the same name
                    return await EnterSingularTemplateManipulationFlowAsync(templateResolutionResult).ConfigureAwait(false);
                }
                else
                {
                    return CreationResultStatus.Cancelled;
                }
            }

            return EnterAmbiguousTemplateManipulationFlow(templateResolutionResult);
        }

        // Attempts to match templates against the inputs.
        private TemplateListResolutionResult QueryForTemplateMatches()
        {
            return TemplateListResolver.PerformCoreTemplateQuery(_settingsLoader.UserTemplateCache.TemplateInfo, _hostDataLoader, _commandInput, _defaultLanguage);
        }

        private async Task<CreationResultStatus> ExecuteAsync()
        {
            // this is checking the initial parse, which is template agnostic.
            if (_commandInput.HasParseError)
            {
                return HandleParseError();
            }

            if (_commandInput.ShowAliasesSpecified)
            {
                return AliasSupport.DisplayAliasValues(EnvironmentSettings, _commandInput, _aliasRegistry, CommandName);
            }

            if (_commandInput.ExpandedExtraArgsFiles && string.IsNullOrEmpty(_commandInput.Alias))
            {   // Only show this if there was no alias expansion.
                // ExpandedExtraArgsFiles must be checked before alias expansion - it'll get reset if there's an alias.
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.ExtraArgsCommandAfterExpansion, string.Join(" ", _commandInput.Tokens)));
            }

            if (string.IsNullOrEmpty(_commandInput.Alias))
            {   // The --alias param is for creating / updating / deleting aliases.
                // If it's not present, try expanding aliases now.
                AliasExpansionStatus aliasExpansionStatus = AliasSupport.TryExpandAliases(_commandInput, _aliasRegistry);
                if (aliasExpansionStatus == AliasExpansionStatus.ExpansionError)
                {
                    Reporter.Output.WriteLine(LocalizableStrings.AliasExpansionError);
                    return CreationResultStatus.InvalidParamValues;
                }
                else if (aliasExpansionStatus == AliasExpansionStatus.Expanded)
                {
                    Reporter.Output.WriteLine(string.Format(LocalizableStrings.AliasCommandAfterExpansion, string.Join(" ", _commandInput.Tokens)));

                    if (_commandInput.HasParseError)
                    {
                        Reporter.Output.WriteLine(LocalizableStrings.AliasExpandedCommandParseError);
                        return HandleParseError();
                    }
                }
                // else NoChange... no special action necessary
            }

            if (!ConfigureLocale())
            {
                return CreationResultStatus.InvalidParamValues;
            }

            Initialize();
            bool forceCacheRebuild = _commandInput.HasDebuggingFlag("--debug:rebuildcache");
            _settingsLoader.RebuildCacheFromSettingsIfNotCurrent(forceCacheRebuild);

            try
            {
                if (!string.IsNullOrEmpty(_commandInput.Alias) && !_commandInput.IsHelpFlagSpecified)
                {
                    return AliasSupport.ManipulateAliasIfValid(_aliasRegistry, _commandInput.Alias, _commandInput.Tokens.ToList(), AllTemplateShortNames);
                }

                if (string.IsNullOrWhiteSpace(TemplateName))
                {
                    return EnterMaintenanceFlow();
                }

                return await EnterTemplateManipulationFlowAsync().ConfigureAwait(false);
            }
            catch (TemplateAuthoringException tae)
            {
                Reporter.Error.WriteLine(tae.Message.Bold().Red());
                return CreationResultStatus.CreateFailed;
            }
        }

        private CreationResultStatus HandleParseError()
        {
            TemplateListResolver.ValidateRemainingParameters(_commandInput, out IReadOnlyList<string> invalidParams);
            DisplayInvalidParameters(invalidParams);

            // TODO: get a meaningful error message from the parser
            if (_commandInput.IsHelpFlagSpecified)
            {
                _telemetryLogger.TrackEvent(CommandName + "-Help");
                ShowUsageHelp();
            }
            else
            {
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.RunHelpForInformationAboutAcceptedParameters, CommandName).Bold().Red());
            }

            return CreationResultStatus.InvalidParamValues;
        }

        private bool ConfigureLocale()
        {
            if (!string.IsNullOrEmpty(_commandInput.Locale))
            {
                string newLocale = _commandInput.Locale;
                if (!ValidateLocaleFormat(newLocale))
                {
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.BadLocaleError, newLocale).Bold().Red());
                    return false;
                }

                EnvironmentSettings.Host.UpdateLocale(newLocale);
                // cache the templates for the new locale
                _settingsLoader.Reload();
            }

            return true;
        }

        // Note: This method explicitly filters out "type" and "language", in addition to other filtering.
        private static IEnumerable<ITemplateParameter> FilterParamsForHelp(IEnumerable<ITemplateParameter> parameterDefinitions, HashSet<string> hiddenParams, bool showImplicitlyHiddenParams = false, bool hasPostActionScriptRunner = false)
        {
            IList<ITemplateParameter> filteredParams = parameterDefinitions
                .Where(x => x.Priority != TemplateParameterPriority.Implicit
                        && !hiddenParams.Contains(x.Name) && !string.Equals(x.Name, "type", StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Name, "language", StringComparison.OrdinalIgnoreCase)
                        && (showImplicitlyHiddenParams || x.DataType != "choice" || x.Choices.Count > 1)).ToList();    // for filtering "tags"

            if (hasPostActionScriptRunner)
            {
                ITemplateParameter allowScriptsParam = new TemplateParameter()
                {
                    Documentation = LocalizableStrings.WhetherToAllowScriptsToRun,
                    Name = "allow-scripts",
                    DataType = "choice",
                    DefaultValue = "prompt",
                    Choices = new Dictionary<string, string>()
                    {
                        { "yes", LocalizableStrings.AllowScriptsYesChoice },
                        { "no", LocalizableStrings.AllowScriptsNoChoice },
                        { "prompt", LocalizableStrings.AllowScriptsPromptChoice }
                    }
                };

                filteredParams.Add(allowScriptsParam);
            }

            return filteredParams;
        }

        private bool GenerateUsageForTemplate(ITemplateInfo templateInfo)
        {
            HostSpecificTemplateData hostTemplateData = _hostDataLoader.ReadHostSpecificTemplateData(templateInfo);

            if(hostTemplateData.UsageExamples != null)
            {
                if(hostTemplateData.UsageExamples.Count == 0)
                {
                    return false;
                }

                Reporter.Output.WriteLine($"    dotnet {CommandName} {templateInfo.ShortName} {hostTemplateData.UsageExamples[0]}");
                return true;
            }

            Reporter.Output.Write($"    dotnet {CommandName} {templateInfo.ShortName}");
            IReadOnlyList<ITemplateParameter> allParameterDefinitions = templateInfo.Parameters;
            IEnumerable<ITemplateParameter> filteredParams = FilterParamsForHelp(allParameterDefinitions, hostTemplateData.HiddenParameterNames);

            foreach (ITemplateParameter parameter in filteredParams)
            {
                if (string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parameter.DefaultValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else if (string.Equals(parameter.DataType, "string", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                else if (string.Equals(parameter.DataType, "choice", StringComparison.OrdinalIgnoreCase) && parameter.Choices.Count == 1)
                {
                    continue;
                }

                string displayParameter = hostTemplateData.DisplayNameForParameter(parameter.Name);

                Reporter.Output.Write($" --{displayParameter}");

                if (!string.IsNullOrEmpty(parameter.DefaultValue) && !string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase))
                {
                    Reporter.Output.Write($" {parameter.DefaultValue}");
                }
            }

            Reporter.Output.WriteLine();
            return true;
        }

        private bool Initialize()
        {
            bool ephemeralHiveFlag = _commandInput.HasDebuggingFlag("--debug:ephemeral-hive");

            if (ephemeralHiveFlag)
            {
                EnvironmentSettings.Host.VirtualizeDirectory(_paths.User.BaseDir);
            }

            bool reinitFlag = _commandInput.HasDebuggingFlag("--debug:reinit");
            if (reinitFlag)
            {
                _paths.Delete(_paths.User.BaseDir);
            }

            // Note: this leaves things in a weird state. Might be related to the localized caches.
            // not sure, need to look into it.
            if (reinitFlag || _commandInput.HasDebuggingFlag("--debug:reset-config"))
            {
                _paths.Delete(_paths.User.AliasesFile);
                _paths.Delete(_paths.User.SettingsFile);
                _settingsLoader.UserTemplateCache.DeleteAllLocaleCacheFiles();
                _settingsLoader.Reload();
                return false;
            }

            if (!_paths.Exists(_paths.User.BaseDir) || !_paths.Exists(_paths.User.FirstRunCookie))
            {
                if (!_commandInput.IsQuietFlagSpecified)
                {
                    Reporter.Output.WriteLine(LocalizableStrings.GettingReady);
                }

                ConfigureEnvironment();
                _paths.WriteAllText(_paths.User.FirstRunCookie, "");
            }

            if (_commandInput.HasDebuggingFlag("--debug:showconfig"))
            {
                ShowConfig();
                return false;
            }

            return true;
        }

        private void ShowParameterHelp(IReadOnlyDictionary<string, string> inputParams, IParameterSet allParams, string additionalInfo, IReadOnlyList<string> invalidParams, HashSet<string> explicitlyHiddenParams,
                    IReadOnlyDictionary<string, IReadOnlyList<string>> groupVariantsForCanonicals, HashSet<string> groupUserParamsWithDefaultValues, bool showImplicitlyHiddenParams, bool hasPostActionScriptRunner)
        {
            if (!string.IsNullOrEmpty(additionalInfo))
            {
                Reporter.Error.WriteLine(additionalInfo.Bold().Red());
                Reporter.Output.WriteLine();
            }

            IEnumerable<ITemplateParameter> filteredParams = FilterParamsForHelp(allParams.ParameterDefinitions, explicitlyHiddenParams, showImplicitlyHiddenParams, hasPostActionScriptRunner);

            if (filteredParams.Any())
            {
                HelpFormatter<ITemplateParameter> formatter = new HelpFormatter<ITemplateParameter>(EnvironmentSettings, filteredParams, 2, null, true);

                formatter.DefineColumn(
                    param =>
                    {
                        string options;
                        if (string.Equals(param.Name, "allow-scripts", StringComparison.OrdinalIgnoreCase))
                        {
                            options = "--" + param.Name;
                        }
                        else
                        {
                            // the key is guaranteed to exist
                            IList<string> variants = groupVariantsForCanonicals[param.Name].ToList();
                            options = string.Join("|", variants.Reverse());
                        }

                        return "  " + options;
                    },
                    LocalizableStrings.Options
                );

                formatter.DefineColumn(delegate (ITemplateParameter param)
                {
                    StringBuilder displayValue = new StringBuilder(255);
                    displayValue.AppendLine(param.Documentation);

                    if (string.Equals(param.DataType, "choice", StringComparison.OrdinalIgnoreCase))
                    {
                        int longestChoiceLength = param.Choices.Keys.Max(x => x.Length);

                        foreach (KeyValuePair<string, string> choiceInfo in param.Choices)
                        {
                            displayValue.Append("    " + choiceInfo.Key.PadRight(longestChoiceLength + 4));

                            if (!string.IsNullOrWhiteSpace(choiceInfo.Value))
                            {
                                displayValue.Append("- " + choiceInfo.Value);
                            }

                            displayValue.AppendLine();
                        }
                    }
                    else
                    {
                        displayValue.Append(param.DataType ?? "string");
                        displayValue.AppendLine(" - " + param.Priority.ToString());
                    }

                    // display the configured value if there is one
                    string configuredValue = null;
                    if (allParams.ResolvedValues.TryGetValue(param, out object resolvedValueObject))
                    {
                        string resolvedValue = resolvedValueObject as string;

                        if (!string.IsNullOrEmpty(resolvedValue)
                            && !string.IsNullOrEmpty(param.DefaultValue)
                            && !string.Equals(param.DefaultValue, resolvedValue))
                        {
                            configuredValue = resolvedValue;
                        }
                    }

                    if (string.IsNullOrEmpty(configuredValue))
                    {
                        // this will catch when the user inputs the default value. The above deliberately skips it on the resolved values.
                        if (string.Equals(param.DataType, "bool", StringComparison.OrdinalIgnoreCase)
                            && groupUserParamsWithDefaultValues.Contains(param.Name))
                        {
                            configuredValue = "true";
                        }
                        else
                        {
                            inputParams.TryGetValue(param.Name, out configuredValue);
                        }
                    }

                    if (!string.IsNullOrEmpty(configuredValue))
                    {
                        string realValue = configuredValue;

                        if (invalidParams.Contains(param.Name) ||
                            (string.Equals(param.DataType, "choice", StringComparison.OrdinalIgnoreCase)
                                && !param.Choices.ContainsKey(configuredValue)))
                        {
                            realValue = realValue.Bold().Red();
                        }
                        else if (allParams.TryGetRuntimeValue(EnvironmentSettings, param.Name, out object runtimeVal) && runtimeVal != null)
                        {
                            realValue = runtimeVal.ToString();
                        }

                        displayValue.AppendLine(string.Format(LocalizableStrings.ConfiguredValue, realValue));
                    }

                    // display the default value if there is one
                    if (!string.IsNullOrEmpty(param.DefaultValue))
                    {
                        displayValue.AppendLine(string.Format(LocalizableStrings.DefaultValue, param.DefaultValue));
                    }

                    return displayValue.ToString();
                }, string.Empty);

                Reporter.Output.WriteLine(formatter.Layout());
            }
            else
            {
                Reporter.Output.WriteLine(LocalizableStrings.NoParameters);
            }
        }

        private void ParseTemplateArgs(ITemplateInfo templateInfo)
        {
            HostSpecificTemplateData hostSpecificTemplateData = _hostDataLoader.ReadHostSpecificTemplateData(templateInfo);
            _commandInput.ReparseForTemplate(templateInfo, hostSpecificTemplateData);
        }

        private string DetermineTemplateContext()
        {
            return _commandInput.TypeFilter?.ToLowerInvariant();
        }

        private HashSet<string> AllTemplateShortNames
        {
            get
            {
                IReadOnlyCollection<IFilteredTemplateInfo> allTemplates = TemplateListResolver.PerformAllTemplatesQuery(_settingsLoader.UserTemplateCache.TemplateInfo, _hostDataLoader);
                HashSet<string> allShortNames = new HashSet<string>(allTemplates.Select(x => x.Info.ShortName));
                return allShortNames;
            }
        }

        private void ShowConfig()
        {
            Reporter.Output.WriteLine(LocalizableStrings.CurrentConfiguration);
            Reporter.Output.WriteLine(" ");
            TableFormatter.Print(EnvironmentSettings.SettingsLoader.MountPoints, LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<MountPointInfo, object>>
            {
                {LocalizableStrings.MountPoints, x => x.Place},
                {LocalizableStrings.Id, x => x.MountPointId},
                {LocalizableStrings.Parent, x => x.ParentMountPointId},
                {LocalizableStrings.Factory, x => x.MountPointFactoryId}
            });

            TableFormatter.Print(EnvironmentSettings.SettingsLoader.Components.OfType<IMountPointFactory>(), LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<IMountPointFactory, object>>
            {
                {LocalizableStrings.MountPointFactories, x => x.Id},
                {LocalizableStrings.Type, x => x.GetType().FullName},
                {LocalizableStrings.Assembly, x => x.GetType().GetTypeInfo().Assembly.FullName}
            });

            TableFormatter.Print(EnvironmentSettings.SettingsLoader.Components.OfType<IGenerator>(), LocalizableStrings.NoItems, "   ", '-', new Dictionary<string, Func<IGenerator, object>>
            {
                {LocalizableStrings.Generators, x => x.Id},
                {LocalizableStrings.Type, x => x.GetType().FullName},
                {LocalizableStrings.Assembly, x => x.GetType().GetTypeInfo().Assembly.FullName}
            });
        }

        private void ShowInvocationExamples(TemplateListResolutionResult templateResolutionResult)
        {
            const int ExamplesToShow = 2;
            IReadOnlyList<string> preferredNameList = new List<string>() { "mvc" };
            int numShown = 0;

            if (templateResolutionResult.CoreMatchedTemplates.Count == 0)
            {
                return;
            }

            List<ITemplateInfo> templateList = templateResolutionResult.CoreMatchedTemplates.Select(x => x.Info).ToList();
            Reporter.Output.WriteLine("Examples:");
            HashSet<string> usedGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string preferredName in preferredNameList)
            {
                ITemplateInfo template = templateList.FirstOrDefault(x => string.Equals(x.ShortName, preferredName, StringComparison.OrdinalIgnoreCase));

                if (template != null)
                {
                    string identity = string.IsNullOrWhiteSpace(template.GroupIdentity) ? string.IsNullOrWhiteSpace(template.Identity) ? string.Empty : template.Identity : template.GroupIdentity;
                    if (usedGroupIds.Add(identity))
                    {
                        GenerateUsageForTemplate(template);
                        numShown++;
                    }
                }

                templateList.Remove(template);  // remove it so it won't get chosen again
            }

            // show up to 2 examples (total, including the above)
            Random rnd = new Random();
            for (int i = numShown; i < ExamplesToShow && templateList.Count > 0; i++)
            {
                int index = rnd.Next(0, templateList.Count - 1);
                ITemplateInfo template = templateList[index];
                string identity = string.IsNullOrWhiteSpace(template.GroupIdentity) ? string.IsNullOrWhiteSpace(template.Identity) ? string.Empty : template.Identity : template.GroupIdentity;
                if (usedGroupIds.Add(identity) && !GenerateUsageForTemplate(template))
                {
                    --i;
                }

                templateList.Remove(template);  // remove it so it won't get chosen again
            }

            // show a help example
            Reporter.Output.WriteLine($"    dotnet {CommandName} --help");
        }

        private void ShowTemplateGroupHelp(IList<ITemplateInfo> templateGroup, bool showImplicitlyHiddenParams = false)
        {
            if (templateGroup.Count == 0)
            {
                return;
            }

            // Use the highest precedence template for most of the output
            ITemplateInfo preferredTemplate = templateGroup.OrderByDescending(x => x.Precedence).First();

            // use all templates to get the language choices
            HashSet<string> languages = new HashSet<string>();
            foreach (ITemplateInfo templateInfo in templateGroup)
            {
                if (templateInfo.Tags != null && templateInfo.Tags.TryGetValue("language", out ICacheTag languageTag))
                {
                    languages.UnionWith(languageTag.ChoicesAndDescriptions.Keys.Where(x => !string.IsNullOrWhiteSpace(x)).ToList());
                }
            }

            if (languages != null && languages.Any())
            {
                Reporter.Output.WriteLine($"{preferredTemplate.Name} ({string.Join(", ", languages)})");
            }
            else
            {
                Reporter.Output.WriteLine(preferredTemplate.Name);
            }

            if (!string.IsNullOrWhiteSpace(preferredTemplate.Author))
            {
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.Author, preferredTemplate.Author));
            }

            if (!string.IsNullOrWhiteSpace(preferredTemplate.Description))
            {
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.Description, preferredTemplate.Description));
            }

            if (!string.IsNullOrEmpty(preferredTemplate.ThirdPartyNotices))
            {
                Reporter.Output.WriteLine(string.Format(LocalizableStrings.ThirdPartyNotices, preferredTemplate.ThirdPartyNotices));
            }

            HashSet<string> groupUserParamsWithInvalidValues = new HashSet<string>(StringComparer.Ordinal);
            bool groupHasPostActionScriptRunner = false;
            List<IParameterSet> parameterSetsForAllTemplatesInGroup = new List<IParameterSet>();
            IDictionary<string, InvalidParameterInfo> invalidParametersForGroup = new Dictionary<string, InvalidParameterInfo>(StringComparer.Ordinal);
            bool firstInList = true;

            Dictionary<string, IReadOnlyList<string>> groupVariantsForCanonicals = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            HashSet<string> groupUserParamsWithDefaultValues = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, bool> parameterHidingDisposition = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (ITemplateInfo templateInfo in templateGroup)
            {
                IReadOnlyList<InvalidParameterInfo> invalidParamsForTemplate = GetTemplateUsageInformation(templateInfo, out IParameterSet allParamsForTemplate, out IReadOnlyList<string> userParamsWithInvalidValues, out IReadOnlyDictionary<string, IReadOnlyList<string>> variantsForCanonicals, out HashSet<string> userParamsWithDefaultValues, out bool hasPostActionScriptRunner);
                HostSpecificTemplateData hostSpecificTemplateData = _hostDataLoader.ReadHostSpecificTemplateData(templateInfo);
                HashSet<string> parametersToExplicitlyHide = hostSpecificTemplateData?.HiddenParameterNames ?? new HashSet<string>(StringComparer.Ordinal);

                foreach (ITemplateParameter parameter in allParamsForTemplate.ParameterDefinitions)
                {
                    //If the parameter has previously been encountered...
                    if (parameterHidingDisposition.TryGetValue(parameter.Name, out bool isCurrentlyHidden))
                    {
                        //...and it was hidden, but it's not hidden in this template in the group,
                        //  remove its hiding, otherwise leave it as is
                        if (isCurrentlyHidden && !parametersToExplicitlyHide.Contains(parameter.Name))
                        {
                            parameterHidingDisposition[parameter.Name] = false;
                        }
                    }
                    //...otherwise, since this is the first time the parameter has been seen,
                    //  its hiding state should be used as the current disposition
                    else
                    {
                        parameterHidingDisposition[parameter.Name] = parametersToExplicitlyHide.Contains(parameter.Name);
                    }
                }

                if (firstInList)
                {
                    invalidParametersForGroup = invalidParamsForTemplate.ToDictionary(x => x.Canonical, x => x);
                    firstInList = false;
                }
                else
                {
                    invalidParametersForGroup = InvalidParameterInfo.IntersectWithExisting(invalidParametersForGroup, invalidParamsForTemplate);
                }

                groupUserParamsWithInvalidValues.IntersectWith(userParamsWithInvalidValues);    // intersect because if the value is valid for any version, it's valid.
                groupHasPostActionScriptRunner |= hasPostActionScriptRunner;
                parameterSetsForAllTemplatesInGroup.Add(allParamsForTemplate);

                // take the variants from the first template that has the canonical
                foreach (KeyValuePair<string, IReadOnlyList<string>> canonicalAndVariants in variantsForCanonicals)
                {
                    if (!groupVariantsForCanonicals.ContainsKey(canonicalAndVariants.Key))
                    {
                        groupVariantsForCanonicals[canonicalAndVariants.Key] = canonicalAndVariants.Value;
                    }
                }

                // If any template says the user input value is the default, include it here.
                groupUserParamsWithDefaultValues.UnionWith(userParamsWithDefaultValues);
            }

            IParameterSet allGroupParameters = new TemplateGroupParameterSet(parameterSetsForAllTemplatesInGroup);
            string parameterErrors = InvalidParameterInfo.InvalidParameterListToString(invalidParametersForGroup.Values.ToList());
            HashSet<string> parametersToHide = new HashSet<string>(parameterHidingDisposition.Where(x => x.Value).Select(x => x.Key), StringComparer.Ordinal);
            ShowParameterHelp(_commandInput.AllTemplateParams, allGroupParameters, parameterErrors, groupUserParamsWithInvalidValues.ToList(), parametersToHide, groupVariantsForCanonicals, groupUserParamsWithDefaultValues, showImplicitlyHiddenParams, groupHasPostActionScriptRunner);
        }

        // Returns true if any partial matches were displayed, false otherwise
        private bool ShowTemplateNameMismatchHelp(TemplateListResolutionResult templateResolutionResult)
        {
            IDictionary<string, IFilteredTemplateInfo> contextProblemMatches = new Dictionary<string, IFilteredTemplateInfo>();
            IDictionary<string, IFilteredTemplateInfo> remainingPartialMatches = new Dictionary<string, IFilteredTemplateInfo>();

            // this filtering / grouping ignores language differences.
            foreach (IFilteredTemplateInfo template in templateResolutionResult.CoreMatchedTemplates)
            {
                if (contextProblemMatches.ContainsKey(template.Info.Name) || remainingPartialMatches.ContainsKey(template.Info.Name))
                {
                    continue;
                }

                if (template.MatchDisposition.Any(x => x.Location == MatchLocation.Context && x.Kind != MatchKind.Exact))
                {
                    contextProblemMatches.Add(template.Info.Name, template);
                }
                else if(template.MatchDisposition.Any(t => t.Location != MatchLocation.Context && t.Kind != MatchKind.Mismatch && t.Kind != MatchKind.Unspecified))
                {
                    remainingPartialMatches.Add(template.Info.Name, template);
                }
            }

            if (contextProblemMatches.Keys.Count + remainingPartialMatches.Keys.Count > 1)
            {
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.AmbiguousInputTemplateName, TemplateName));
            }
            else if (contextProblemMatches.Keys.Count + remainingPartialMatches.Keys.Count == 0)
            {
                Reporter.Error.WriteLine(string.Format(LocalizableStrings.NoTemplatesMatchName, TemplateName));
                Reporter.Error.WriteLine();
                return false;
            }

            foreach (IFilteredTemplateInfo template in contextProblemMatches.Values)
            {
                if (template.Info.Tags != null && template.Info.Tags.TryGetValue("type", out ICacheTag typeTag))
                {
                    MatchInfo? matchInfo = WellKnownSearchFilters.ContextFilter(DetermineTemplateContext())(template.Info);
                    if ((matchInfo?.Kind ?? MatchKind.Mismatch) == MatchKind.Mismatch)
                    {
                        Reporter.Error.WriteLine(string.Format(LocalizableStrings.TemplateNotValidGivenTheSpecifiedFilter, template.Info.Name).Bold().Red());
                    }
                }
                else
                {   // this really shouldn't ever happen. But better to have a generic error than quietly ignore the partial match.
                    Reporter.Error.WriteLine(string.Format(LocalizableStrings.GenericPlaceholderTemplateContextError, template.Info.Name).Bold().Red());
                }
            }

            if (remainingPartialMatches.Keys.Count > 0)
            {
                Reporter.Error.WriteLine(LocalizableStrings.TemplateMultiplePartialNameMatches.Bold().Red());
            }

            Reporter.Error.WriteLine();
            return true;
        }

        private void ShowUsageHelp()
        {
            Reporter.Output.WriteLine(_commandInput.HelpText);
            Reporter.Output.WriteLine();
        }

        private void DisplayInvalidParameters(IReadOnlyList<string> invalidParams)
        {
            if (invalidParams.Count > 0)
            {
                Reporter.Error.WriteLine(LocalizableStrings.InvalidInputSwitch.Bold().Red());
                foreach (string flag in invalidParams)
                {
                    Reporter.Error.WriteLine($"  {flag}".Bold().Red());
                }
            }
        }

        private static bool ValidateLocaleFormat(string localeToCheck)
        {
            return LocaleFormatRegex.IsMatch(localeToCheck);
        }
    }
}
