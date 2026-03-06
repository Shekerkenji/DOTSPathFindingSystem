using UnityEngine;

public class Mover : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private Vector3 targetPosition;
    [SerializeField] private float speed = 5f;

    private void Update()
    {
        transform.position = Vector3.MoveTowards(
            transform.position,
            targetPosition,
            speed * Time.deltaTime
        );

        // Check arrival
        if (Vector3.SqrMagnitude(transform.position - targetPosition) < 0.001f)
        {
            Destroy(gameObject);
        }
    }
}