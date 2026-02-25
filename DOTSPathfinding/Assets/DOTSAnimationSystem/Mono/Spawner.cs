using UnityEngine;
using System.Collections;

public class LaneSpawner : MonoBehaviour
{
    [Header("Prefabs Per Lane")]
    [SerializeField] private GameObject lane1Prefab;
    [SerializeField] private GameObject lane2Prefab;
    [SerializeField] private GameObject lane3Prefab;

    [Header("Spawn Settings")]
    [SerializeField] private float spawnInterval = 2f;

    [Header("Lane Math")]
    [SerializeField] private float baseX = -2f;
    [SerializeField] private float laneSpacing = 2f;
    [SerializeField] private float spawnY = 0f;
    [SerializeField] private float spawnZ = 0f;

    private Coroutine spawnRoutine;

    private void OnEnable()
    {
        spawnRoutine = StartCoroutine(SpawnLoop());
    }

    private void OnDisable()
    {
        if (spawnRoutine != null)
            StopCoroutine(spawnRoutine);
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            SpawnLane(lane1Prefab, 0);
            SpawnLane(lane2Prefab, 1);
            SpawnLane(lane3Prefab, 2);

            yield return new WaitForSeconds(spawnInterval);
        }
    }

    private void SpawnLane(GameObject prefab, int laneIndex)
    {
        if (prefab == null)
            return;

        float x = baseX + laneIndex * laneSpacing;
        Vector3 pos = new Vector3(x, spawnY, spawnZ);

        Instantiate(prefab, pos, Quaternion.identity);
    }
}