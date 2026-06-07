using UnityEngine;
using UnityEngine.EventSystems; 

public class RDInteraction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerExitHandler
{
    [Header("References")]
    [SerializeField] private CustomRenderTexture rdTexture;

    [Header("Interaction Settings")]
    [SerializeField] private float baseRadius = 0.01f;
    [SerializeField] private float maxRadius = 0.1f;
    [SerializeField] private float baseFeed = 0.055f;
    [SerializeField] private float maxFeed = 0.08f;

    [Header("Random Speed Settings")]
    [SerializeField] private float minRadiusSpeed = 0.02f;
    [SerializeField] private float maxRadiusSpeed = 0.15f;
    [SerializeField] private float minFeedSpeed = 0.005f;
    [SerializeField] private float maxFeedSpeed = 0.03f;

    private float currentFeed;
    private float currentRadius;

    private Material rdMaterial;
    private bool isInteracting = false;
    private Vector2 currentUV;

    void Start()
    {
        if (rdTexture != null) rdMaterial = rdTexture.material;
        currentFeed = baseFeed;
        currentRadius = baseRadius;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isInteracting = true;
        UpdateUV(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        UpdateUV(eventData);
    }

    private void UpdateUV(PointerEventData eventData)
    {
        Camera cam = eventData.pressEventCamera ?? Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ScreenPointToRay(eventData.position);
            // Use RaycastAll to bypass any BoxColliders and force finding a MeshCollider
            RaycastHit[] hits = Physics.RaycastAll(ray);
            foreach (var hit in hits)
            {
                if (hit.collider.gameObject == this.gameObject && hit.collider is MeshCollider)
                {
                    Vector2 uv = hit.textureCoord;
                    
                    currentUV = uv;
                    return; // Found correct UV, end search
                }
            }
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isInteracting = false;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        isInteracting = false;
    }

    void Update()
    {
        if (rdMaterial == null) return;

        if (isInteracting)
        {
            // Generate completely random speed each frame (without Lerp)
            float randomRadiusSpeed = Random.Range(minRadiusSpeed, maxRadiusSpeed);
            float randomFeedSpeed = Random.Range(minFeedSpeed, maxFeedSpeed);
            // Random increase
            currentRadius += randomRadiusSpeed * Time.deltaTime;
            currentFeed += randomFeedSpeed * Time.deltaTime;
            
            rdMaterial.SetFloat("_ClickState", 1f);
            rdMaterial.SetVector("_ClickPos", currentUV);
        }
        else
        {
            // Generate completely random speed each frame (without Lerp)
            float randomRadiusSpeed = Random.Range(minRadiusSpeed, maxRadiusSpeed);
            float randomFeedSpeed = Random.Range(minFeedSpeed, maxFeedSpeed);
            // Random decrease
            currentRadius -= randomRadiusSpeed * Time.deltaTime;
            currentFeed -= randomFeedSpeed * Time.deltaTime;
            
            rdMaterial.SetFloat("_ClickState", 0f);
        }

        // Clamp between base and max values
        currentRadius = Mathf.Clamp(currentRadius, baseRadius, maxRadius);
        currentFeed = Mathf.Clamp(currentFeed, baseFeed, maxFeed);

        // Update final material parameters
        rdMaterial.SetFloat("_ClickRadius", currentRadius);
        rdMaterial.SetFloat("_Feed", currentFeed);
    }
}
