using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class UnityCliEditModeTests
{
    [Test]
    public void MainCameraExistsInScene()
    {
        Assert.IsNotNull(GameObject.Find("Main Camera"));
    }

    [Test]
    public void CliUiProbeScriptAssetExists()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/Scripts/CliUiProbe.cs"));
    }
}
