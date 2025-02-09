﻿//using CustomizePlus.UI.Windows.Debug;
using Dalamud.Interface;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Interface.Utility;
using ImGuiNET;
using OtterGui.Classes;
using OtterGui;
using OtterGui.Raii;
using OtterGui.Widgets;
using System.Diagnostics;
using System.Numerics;
using CustomizePlus.Core.Services;
using CustomizePlus.Configuration.Data;
using CustomizePlus.Profiles;
using CustomizePlus.Templates;
using CustomizePlus.Core.Helpers;

namespace CustomizePlus.UI.Windows.MainWindow.Tabs;

public class SettingsTab
{
    private const uint DiscordColor = 0xFFDA8972;

    private readonly PluginConfiguration _configuration;
    private readonly TemplateManager _templateManager;
    private readonly ProfileManager _profileManager;
    private readonly HookingService _hookingService;
    private readonly SaveService _saveService;
    private readonly TemplateEditorManager _templateEditorManager;
    private readonly CPlusChangeLog _changeLog;
    private readonly MessageService _messageService;

    public SettingsTab(
        PluginConfiguration configuration,
        TemplateManager templateManager,
        ProfileManager profileManager,
        HookingService hookingService,
        SaveService saveService,
        TemplateEditorManager templateEditorManager,
        CPlusChangeLog changeLog,
        MessageService messageService)
    {
        _configuration = configuration;
        _templateManager = templateManager;
        _profileManager = profileManager;
        _hookingService = hookingService;
        _saveService = saveService;
        _templateEditorManager = templateEditorManager;
        _changeLog = changeLog;
        _messageService = messageService;
    }

    public void Draw()
    {
        using var child = ImRaii.Child("MainWindowChild");
        if (!child)
            return;

        DrawGeneralSettings();

        ImGui.NewLine();
        ImGui.NewLine();

        using (var child2 = ImRaii.Child("SettingsChild"))
        {
            DrawInterface();
            DrawCommands();
            DrawAdvancedSettings();
        }

        DrawSupportButtons();
    }

    #region General Settings
    // General Settings
    private void DrawGeneralSettings()
    {
        DrawPluginEnabledCheckbox();
    }

    private void DrawPluginEnabledCheckbox()
    {
        using (var disabled = ImRaii.Disabled(_templateEditorManager.IsEditorActive))
        {
            var isChecked = _configuration.PluginEnabled;

            //users doesn't really need to know what exactly this checkbox does so we just tell them it toggles all profiles
            if (CtrlHelper.CheckboxWithTextAndHelp("##pluginenabled", "Enable Customize+",
                    "Globally enables or disables all plugin functionality.", ref isChecked))
            {
                _configuration.PluginEnabled = isChecked;
                _configuration.Save();
                _hookingService.ReloadHooks();
            }
        }
    }
    #endregion

    #region Chat Commands Settings
    private void DrawCommands()
    {
        var isShouldDraw = ImGui.CollapsingHeader("Chat Commands");

        if (!isShouldDraw)
            return;

        DrawPrintSuccessMessages();
    }

    private void DrawPrintSuccessMessages()
    {
        var isChecked = _configuration.CommandSettings.PrintSuccessMessages;

        if (CtrlHelper.CheckboxWithTextAndHelp("##displaychatcommandconfirms", "Print Successful Command Execution Messages to Chat",
                "Controls whether successful execution of chat commands will be acknowledged by separate chat message or not.", ref isChecked))
        {
            _configuration.CommandSettings.PrintSuccessMessages = isChecked;
            _configuration.Save();
        }
    }
    #endregion

    #region Interface Settings

    private void DrawInterface()
    {
        var isShouldDraw = ImGui.CollapsingHeader("Interface");

        if (!isShouldDraw)
            return;

        DrawHideWindowInCutscene();
        DrawFoldersDefaultOpen();

        if (Widget.DoubleModifierSelector("Template Deletion Modifier",
            "A modifier you need to hold while clicking the Delete Template button for it to take effect.", 100 * ImGuiHelpers.GlobalScale,
            _configuration.UISettings.DeleteTemplateModifier, v => _configuration.UISettings.DeleteTemplateModifier = v))
            _configuration.Save();
    }

    private void DrawHideWindowInCutscene()
    {
        var isChecked = _configuration.UISettings.HideWindowInCutscene;

        if (CtrlHelper.CheckboxWithTextAndHelp("##hidewindowincutscene", "Hide Plugin Windows in Cutscenes",
                "Controls whether any Customize+ windows are hidden during cutscenes or not.", ref isChecked))
        {
            _configuration.UISettings.HideWindowInCutscene = isChecked;
            _configuration.Save();
        }
    }

    private void DrawFoldersDefaultOpen()
    {
        var isChecked = _configuration.UISettings.FoldersDefaultOpen;

        if (CtrlHelper.CheckboxWithTextAndHelp("##foldersdefaultopen", "Open All Folders by Default",
                "Controls whether folders in template and profile lists are open by default or not.", ref isChecked))
        {
            _configuration.UISettings.FoldersDefaultOpen = isChecked;
            _configuration.Save();
        }
    }

    #endregion

    #region Advanced Settings
    // Advanced Settings
    private void DrawAdvancedSettings()
    {
        var isShouldDraw = ImGui.CollapsingHeader("Advanced");

        if (!isShouldDraw)
            return;

        ImGui.NewLine();
        CtrlHelper.LabelWithIcon(FontAwesomeIcon.ExclamationTriangle,
            "These are advanced settings. Enable them at your own risk.");
        ImGui.NewLine();

        DrawEnableRootPositionCheckbox();
        DrawDebugModeCheckbox();
    }

    private void DrawEnableRootPositionCheckbox()
    {
        var isChecked = _configuration.EditorConfiguration.RootPositionEditingEnabled;
        if (CtrlHelper.CheckboxWithTextAndHelp("##rootpos", "Root editing",
                "Enables ability to edit the root bones.", ref isChecked))
        {
            _configuration.EditorConfiguration.RootPositionEditingEnabled = isChecked;
            _configuration.Save();
        }
    }

    private void DrawDebugModeCheckbox()
    {
        var isChecked = _configuration.DebuggingModeEnabled;
        if (CtrlHelper.CheckboxWithTextAndHelp("##debugmode", "Debug mode",
                "Enables debug mode", ref isChecked))
        {
            _configuration.DebuggingModeEnabled = isChecked;
            _configuration.Save();
        }
    }

    #endregion

    #region Support Area
    private void DrawSupportButtons()
    {
        var width = ImGui.CalcTextSize("Join Discord for Support").X + ImGui.GetStyle().FramePadding.X * 2;
        var xPos = ImGui.GetWindowWidth() - width;
        // Respect the scroll bar width.
        if (ImGui.GetScrollMaxY() > 0)
            xPos -= ImGui.GetStyle().ScrollbarSize + ImGui.GetStyle().FramePadding.X;

        ImGui.SetCursorPos(new Vector2(xPos, 0));
        DrawDiscordButton(width);

        ImGui.SetCursorPos(new Vector2(xPos, 1 * ImGui.GetFrameHeightWithSpacing()));
        if (ImGui.Button("Show update history", new Vector2(width, 0)))
            _changeLog.Changelog.ForceOpen = true;
    }

    /// <summary> Draw a button to open the official discord server. </summary>
    private void DrawDiscordButton(float width)
    {
        const string address = @"https://discord.gg/KvGJCCnG8t";
        using var color = ImRaii.PushColor(ImGuiCol.Button, DiscordColor);
        if (ImGui.Button("Join Discord for Support", new Vector2(width, 0)))
            try
            {
                var process = new ProcessStartInfo(address)
                {
                    UseShellExecute = true,
                };
                Process.Start(process);
            }
            catch
            {
                _messageService.NotificationMessage($"Unable to open Discord at {address}.", NotificationType.Error, false);
            }

        ImGuiUtil.HoverTooltip($"Open {address}");
    }
    #endregion
}
