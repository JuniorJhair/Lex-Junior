using System;
using System.IO;
using System.Windows;
using System.Windows.Shell;

namespace LexBrowser.Core;

public static class JumpListManager
{
    public static void Initialize()
    {
        try
        {
            var jumpList = new JumpList
            {
                ShowFrequentCategory = false,
                ShowRecentCategory = true
            };

            string exePath = Environment.ProcessPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LexJuniorBrowser", "LexBrowser.exe");

            var taskNewTab = new JumpTask
            {
                Title = "Nueva pestaña",
                Description = "Abrir una nueva pestaña normal en Lex Junior",
                ApplicationPath = exePath,
                Arguments = "--new-tab",
                IconResourcePath = exePath,
                IconResourceIndex = 0
            };

            var taskIncognito = new JumpTask
            {
                Title = "Nueva pestaña de incógnito",
                Description = "Navegar de forma privada sin guardar historial ni cookies",
                ApplicationPath = exePath,
                Arguments = "--incognito",
                IconResourcePath = exePath,
                IconResourceIndex = 0
            };

            var taskGuest = new JumpTask
            {
                Title = "Modo Invitado",
                Description = "Sesión aislada para invitados con protección de contraseñas e historial",
                ApplicationPath = exePath,
                Arguments = "--guest",
                IconResourcePath = exePath,
                IconResourceIndex = 0
            };

            jumpList.JumpItems.Add(taskNewTab);
            jumpList.JumpItems.Add(taskIncognito);
            jumpList.JumpItems.Add(taskGuest);

            JumpList.SetJumpList(Application.Current, jumpList);
            jumpList.Apply();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JumpList] Error: {ex.Message}");
        }
    }
}
