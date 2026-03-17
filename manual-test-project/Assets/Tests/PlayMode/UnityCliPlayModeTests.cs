using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class UnityCliPlayModeTests
{
    [UnityTest]
    public IEnumerator CreatedObjectRemainsAvailableAcrossAFrame()
    {
        var probe = new GameObject("UnityCliPlayModeProbe");
        yield return null;
        Assert.IsNotNull(GameObject.Find("UnityCliPlayModeProbe"));
        Object.Destroy(probe);
    }
}
