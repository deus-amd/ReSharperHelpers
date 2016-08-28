﻿using System;
using System.Collections.Generic;
using System.Linq;
using AlexPovar.ReSharperHelpers.Helpers;
using AlexPovar.ReSharperHelpers.Settings;
using JetBrains.Annotations;
using JetBrains.Application.Progress;
using JetBrains.Application.Settings;
using JetBrains.Application.Settings.Store.Implementation;
using JetBrains.DocumentManagers.impl;
using JetBrains.DocumentManagers.Transactions;
using JetBrains.IDE;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.ContextActions;
using JetBrains.ReSharper.Feature.Services.CSharp.Analyses.Bulbs;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.FileTemplates;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.Settings;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.Templates;
using JetBrains.ReSharper.Feature.Services.Util;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.TextControl;
using JetBrains.Util;
using JetBrains.Util.Extension;

namespace AlexPovar.ReSharperHelpers.ContextActions
{
  [ContextAction(Group = "C#", Name = "[AlexHelpers] Create or browse test file", Description = "Creates new or opens the existing test file.", Priority = short.MinValue)]
  public class CreateOrBrowseTestFileAction : IBulbAction, IContextAction
  {
    private const string TemplateDescription = "[AlexHelpers] TestFile";

    [NotNull] private readonly ICSharpContextActionDataProvider _myProvider;

    public CreateOrBrowseTestFileAction([NotNull] ICSharpContextActionDataProvider provider)
    {
      if (provider == null) throw new ArgumentNullException(nameof(provider));

      this._myProvider = provider;

      this.IsEnabledForProject = ResolveIsEnabledForProvider(provider);
    }

    private bool IsEnabledForProject { get; }

    [CanBeNull]
    private IProjectFile ExistingProjectFile { get; set; }

    public void Execute(ISolution solution, ITextControl textControl)
    {
      if (this.ExistingProjectFile != null)
      {
        ShowProjectFile(solution, this.ExistingProjectFile, null);
        return;
      }

      using (ReadLockCookie.Create())
      {
        string testClassName;
        string testNamespace;
        string testFileName;
        IProjectFolder testFolder;
        Template testFileTemplate;

        using (var cookie = solution.CreateTransactionCookie(DefaultAction.Rollback, this.Text, NullProgressIndicator.Instance))
        {
          var declaration = this._myProvider.GetSelectedElement<ICSharpTypeDeclaration>();
          var declaredType = declaration?.DeclaredElement;
          if (declaredType == null) return;

          var settingsStore = declaration.GetSettingsStore();

          var helperSettings = settingsStore.GetKey<ReSharperHelperSettings>(SettingsOptimization.OptimizeDefault);

          var projectName = helperSettings.TestsProjectName;
          if (projectName.IsNullOrEmpty())
          {
            MessageBox.ShowError($"The test project value is not configured.{Environment.NewLine}Specify project name in configuration.", "ReSharper Helpers");
            return;
          }

          var project = solution.GetProjectsByName(projectName).FirstOrDefault();
          if (project == null)
          {
            MessageBox.ShowError($"Unable to find '{projectName}' project. Ensure project name is correct", "ReSharper Helpers");
            return;
          }

          var originalNamespaceParts = TrimDefaultProjectNamespace(declaration.GetProject(), declaredType.GetContainingNamespace().QualifiedName);
          var testFolderLocation = originalNamespaceParts.Aggregate(project.Location, (current, part) => current.Combine(part));

          testNamespace = StringUtil.MakeFQName(project.GetDefaultNamespace(), StringUtil.MakeFQName(originalNamespaceParts));

          testFolder = project.GetOrCreateProjectFolder(testFolderLocation, cookie);
          if (testFolder == null) return;

          testClassName = MakeTestClassName(declaredType.ShortName);
          testFileName = testClassName + ".cs";

          testFileTemplate = StoredTemplatesProvider.Instance.EnumerateTemplates(settingsStore, TemplateApplicability.File).FirstOrDefault(t => t.Description == TemplateDescription);

          cookie.Commit(NullProgressIndicator.Instance);
        }

        if (testFileTemplate != null)
        {
          FileTemplatesManager.Instance.CreateFileFromTemplate(testFileName, new ProjectFolderWithLocation(testFolder), testFileTemplate);
        }
        else
        {
          var newFile = AddNewItemUtil.AddFile(testFolder, testFileName);
          int? caretPosition = -1;

          solution.GetPsiServices().Transactions.Execute(this.Text, () =>
          {
            var psiSourceFile = newFile.ToSourceFile();

            var csharpFile = psiSourceFile?.GetDominantPsiFile<CSharpLanguage>() as ICSharpFile;
            if (csharpFile == null) return;

            var elementFactory = CSharpElementFactory.GetInstance(csharpFile);

            var namespaceDeclaration = elementFactory.CreateNamespaceDeclaration(testNamespace);
            var addedNs = csharpFile.AddNamespaceDeclarationAfter(namespaceDeclaration, null);

            var classLikeDeclaration = (IClassLikeDeclaration) elementFactory.CreateTypeMemberDeclaration("public class $0 {}", testClassName);
            var addedTypeDeclaration = addedNs.AddTypeDeclarationAfter(classLikeDeclaration, null) as IClassDeclaration;

            caretPosition = addedTypeDeclaration?.Body?.GetDocumentRange().TextRange.StartOffset + 1;
          });

          ShowProjectFile(solution, newFile, caretPosition);
        }
      }
    }

    public string Text => this.ExistingProjectFile == null ? "[Helpers] Create test file" : "[Helpers] Go to test file";

    public IEnumerable<IntentionAction> CreateBulbItems()
    {
      return this.ToContextActionIntentions(HelperActionsConstants.ContextActionsAnchor, MyIcons.ContextActionIcon);
    }

    public bool IsAvailable(IUserDataHolder cache)
    {
      this.ExistingProjectFile = null;
      if (!this.IsEnabledForProject) return false;

      var classDeclaration = this._myProvider.GetSelectedElement<ICSharpIdentifier>()?.Parent as IClassDeclaration;
      if (classDeclaration == null) return false;

      //Disable for nested classes
      if (classDeclaration.GetContainingTypeDeclaration() != null) return false;

      var declaredElement = classDeclaration.DeclaredElement;
      if (declaredElement == null) return false;

      //TRY RESOLVE EXISTING TEST
      var symbolScope = classDeclaration.GetPsiServices().Symbols.GetSymbolScope(LibrarySymbolScope.NONE, true);

      var typeName = declaredElement.ShortName;
      var alreadyDeclaredClasses = symbolScope.GetElementsByShortName(MakeTestClassName(typeName)).OfType<IClass>().Where(c => c != null).ToArray();
      if (alreadyDeclaredClasses.Length == 0) return true;

      var myProject = classDeclaration.GetProject();
      if (myProject == null) return true;

      var expectedNamespaceParts = TrimDefaultProjectNamespace(myProject, declaredElement.GetContainingNamespace().QualifiedName);

      var exactMatchTestClass = alreadyDeclaredClasses
        .Where(
          testCandidateClass =>
          {
            var testProj = (testCandidateClass.Module as IProjectPsiModule)?.Project;
            if (testProj == null) return false;

            var actualNamespaceParts = TrimDefaultProjectNamespace(testProj, testCandidateClass.GetContainingNamespace().QualifiedName);
            if (actualNamespaceParts.Length != expectedNamespaceParts.Length) return false;

            return expectedNamespaceParts.SequenceEqual(actualNamespaceParts, StringComparer.Ordinal);
          })
        .FirstOrDefault();

      this.ExistingProjectFile = exactMatchTestClass?.GetSingleOrDefaultSourceFile()?.ToProjectFile();

      return true;
    }

    [NotNull]
    private static string MakeTestClassName([NotNull] string className) => className + "Tests";

    private static bool ResolveIsEnabledForProvider([NotNull] ICSharpContextActionDataProvider provider)
    {
      var project = provider.Project;
      if (project == null) return false;

      var dataContext = project.ToDataContext();
      var contextRange = ContextRange.Smart(dataContext);

      var settingsStore = Shell.Instance.GetComponent<SettingsStore>();

      var settingsStoreBound = settingsStore.BindToContextTransient(contextRange);
      var mySettings = settingsStoreBound.GetKey<ReSharperHelperSettings>(SettingsOptimization.OptimizeDefault);

      return !project.Name.Equals(mySettings.TestsProjectName);
    }


    [NotNull]
    private static string[] TrimDefaultProjectNamespace([NotNull] IProject project, [NotNull] string classNamespace)
    {
      var namespaceParts = StringUtil.FullySplitFQName(classNamespace);

      var defaultNamespace = project.GetDefaultNamespace();
      if (defaultNamespace != null)
      {
        var parts = StringUtil.FullySplitFQName(defaultNamespace);

        namespaceParts = namespaceParts.SkipWhile((part, index) => index < parts.Length && part.Equals(parts[index], StringComparison.Ordinal)).ToArray();
      }

      return namespaceParts;
    }

    private static void ShowProjectFile([NotNull] ISolution solution, [NotNull] IProjectFile file, int? caretPosition)
    {
      var editor = solution.GetComponent<IEditorManager>();
      var textControl = editor.OpenProjectFile(file, true);

      if (caretPosition != null) textControl?.Caret.MoveTo(caretPosition.Value, CaretVisualPlacement.DontScrollIfVisible);
    }
  }
}