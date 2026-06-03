using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public class SnowTrailPainter : MonoBehaviour
{
    [Header("References")]
    public SplineContainer splineContainer;
    public Transform sled;

    [Header("Settings")]
    public float roadHalfWidth = 2f;
    public float brushWidth     = 0.04f;  // ancho del trail (eje U)
    public float brushLength    = 0.01f;  // largo del trail (eje V) - más chico = menos smear
    public float brushStrength = 1f;
    public int renderTexSize = 512;

    [Header("Detection")]
    public float maxSurfaceDistance = 0.5f; // ajustá según la escala de tu sled

    [Header("Trail Offset")]
    [Tooltip("Cuánto retrocede el trail en UV space. Subí hasta que desaparezca el parche blanco delante.")]
    public float trailBackOffset = 0.02f;

    Vector2 _prevUV = new Vector2(-1f, -1f);

    MeshCollider  _meshCollider;
    Material _brushMat;
    RenderTexture _rt;
    [SerializeField]
    bool _wasOnSplineLastFrame = false;

    void Awake()
    {
        // Crea RT propia para esta spline
        _rt = new RenderTexture(renderTexSize, renderTexSize, 0, RenderTextureFormat.ARGB32);
        _rt.wrapMode          = TextureWrapMode.Clamp;
        _rt.filterMode        = FilterMode.Point;
        _rt.enableRandomWrite = true;
        _rt.Create();

        // Limpia la RT
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = prev;

        // Clona el material y asigna la RT exclusiva
        splineContainer = GetComponentInParent<SplineContainer>();
        var meshRenderer = splineContainer.GetComponent<MeshRenderer>();
        var matInstance  = new Material(meshRenderer.sharedMaterial);
        matInstance.SetTexture("_TrailTex", _rt);
        meshRenderer.material = matInstance;

        _meshCollider  = splineContainer.GetComponent<MeshCollider>();
        _brushMat = new Material(Shader.Find("Custom/SnowBrush_Clean"));
    }

    void Update()
    {
        float3 localPos = splineContainer.transform.InverseTransformPoint(sled.position);
        SplineUtility.GetNearestPoint(splineContainer.Spline, localPos,
            out float3 nearest, out float t);

        
        if (!IsSleddingOnThisSpline())
        {
            _wasOnSplineLastFrame = false;
            _prevUV = new Vector2(-1f, -1f);
            return;
        }

        Vector2 uv = ComputeUV(localPos, nearest, t);

        if (!_wasOnSplineLastFrame)
        {
            _wasOnSplineLastFrame = true;
            _prevUV = uv;
            return;
        }

        if (_prevUV.x >= 0f)
        {
            // Aplicamos el offset hacia atrás en UV V
            Vector2 paintUV = new Vector2(_prevUV.x, _prevUV.y + trailBackOffset);

            RenderTexture temp = RenderTexture.GetTemporary(_rt.descriptor);
            _brushMat.SetVector("_BrushUV",     new Vector4(paintUV.x, paintUV.y, 0, 0));
            _brushMat.SetFloat ("_BrushWidth",  brushWidth);
            _brushMat.SetFloat ("_BrushLength", brushLength);
            _brushMat.SetFloat ("_Strength",    brushStrength);
            Graphics.Blit(_rt, temp, _brushMat);
            Graphics.Blit(temp, _rt);
            RenderTexture.ReleaseTemporary(temp);
        }

        _prevUV = uv;
    }

    bool IsSleddingOnThisSpline()
    {
        Collider[] hits = Physics.OverlapSphere(sled.position, maxSurfaceDistance);
        foreach (var hit in hits)
            if (hit == _meshCollider) return true;
        return false;
    }

    Vector2 ComputeUV(float3 localPos, float3 nearest, float t)
    {
        float3 tangent  = math.normalize(splineContainer.Spline.EvaluateTangent(t));
        float3 up       = splineContainer.Spline.EvaluateUpVector(t);
        float3 right    = math.cross(up, tangent);
        float  lateral  = math.dot(localPos - nearest, right);
        float  u        = (lateral / roadHalfWidth) * 0.5f + 0.5f;
        return new Vector2(u, t);
    }

    void OnDestroy()
    {
        if (_rt != null) _rt.Release();
    }
}