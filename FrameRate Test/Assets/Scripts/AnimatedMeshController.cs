using UnityEngine;

public class AnimatedMeshController : MonoBehaviour
{
    AnimatedMesh animator;
    private void Awake()
    {
        animator = GetComponent<AnimatedMesh>();
    }
    public void Start()
    {
        ActivateAll();
    }

    public void DeactivateAll()
    {
            animator.enabled = false;
            animator.GetComponentInChildren<MeshRenderer>().enabled = false;
        
    }

    public void ActivateAll()
    {

            animator.enabled = true;
            animator.GetComponentInChildren<MeshRenderer>().enabled = true;
            animator.Play("mixamo.com");
        
    }

   
}
