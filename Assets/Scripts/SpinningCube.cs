using System.Collections.Generic;
using System.Collections;
using UnityEngine;

public class SpinningCube : MonoBehaviour
{
    IEnumerator Rotate()
    {
        while (true)
        {
            transform.Rotate(0, 30, 0);
            yield return new WaitForSeconds(5);
        }
    }

    void Start()
    {
        StartCoroutine(Rotate());
    }
}