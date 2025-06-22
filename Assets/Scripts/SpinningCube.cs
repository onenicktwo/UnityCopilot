using UnityEngine;
using System.Collections;

public class SpinningCube : MonoBehaviour
{
    private float speed = 30f;
    private IEnumerator Start()
    {
        float t = 0f;
        while (true)
        {
            bool reverse = t >= 5f;
            transform.Rotate(Vector3.up, (reverse ? -speed : speed) * Time.deltaTime);
            t += Time.deltaTime;
            yield return null;
        }
    }
}