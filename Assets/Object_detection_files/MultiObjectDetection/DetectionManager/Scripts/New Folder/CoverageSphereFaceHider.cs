using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
public class CoverageSphereFaceHider : MonoBehaviour
{
    [Header("Look-to-hide settings")]
    public float maxRayDistance = 10f;
    public float hitRadiusMeters = 0.02f;   // how wide the "gaze brush" is on the sphere surface
    public float facingDotThreshold = 0.5f; // triangle must face camera a bit (0..1)
    public int maxFacesHidePerFrame = 1;   // performance cap

    Mesh _mesh;
    Vector3[] _verts;
    int[] _tris;
    Color[] _colors;
    


    Vector3[] _faceCenters;   // local space
    Vector3[] _faceNormals;   // local space
    bool[] _hidden;

    Camera _cam;


    [Header("Startup delay")]
    public float startDelaySeconds = 0.8f; // delay before hiding starts
    private float _timeSinceSpawn = 0f;
    private bool _canHide = false;











    void Awake()
    {
        _cam = Camera.main;
        PrepareMeshForPerFaceHiding();
        _timeSinceSpawn = 0f;
        _canHide = false;
    }

    void PrepareMeshForPerFaceHiding()
    {
        var mf = GetComponent<MeshFilter>();
        _mesh = mf.mesh; // instance

        // Ensure triangles can be controlled per-face:
        // Duplicate vertices so each triangle has unique vertices.
        var oldVerts = _mesh.vertices;
        var oldTris = _mesh.triangles;

        var newVerts = new Vector3[oldTris.Length];
        var newTris  = new int[oldTris.Length];
        var newUv2   = new Vector2[oldTris.Length]; //  barycentric coords in UV2

        for (int i = 0; i < oldTris.Length; i += 3)
        {
            // duplicate triangle vertices
            newVerts[i + 0] = oldVerts[oldTris[i + 0]];
            newVerts[i + 1] = oldVerts[oldTris[i + 1]];
            newVerts[i + 2] = oldVerts[oldTris[i + 2]];

            // sequential indices
            newTris[i + 0] = i + 0;
            newTris[i + 1] = i + 1;
            newTris[i + 2] = i + 2;

            //  barycentric values (store x,y; z is derived in shader)
            newUv2[i + 0] = new Vector2(1f, 0f);
            newUv2[i + 1] = new Vector2(0f, 1f);
            newUv2[i + 2] = new Vector2(0f, 0f);
        }

        _mesh.Clear();
        _mesh.vertices  = newVerts;
        _mesh.triangles = newTris;
        _mesh.uv2       = newUv2;  //  IMPORTANT LINE
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();


        _verts = _mesh.vertices;
        _tris  = _mesh.triangles;

        // Init colors (start visible). You can set initial alpha here too.
        _colors = new Color[_verts.Length];
        for (int i = 0; i < _colors.Length; i++)
            _colors[i] = new Color(0.25f, 0.55f, 1.0f, 0.25f);



        _mesh.colors = _colors;

        int faceCount = _tris.Length / 3;
        _faceCenters = new Vector3[faceCount];
        _faceNormals = new Vector3[faceCount];
        _hidden = new bool[faceCount];

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = _tris[f * 3 + 0];
            int i1 = _tris[f * 3 + 1];
            int i2 = _tris[f * 3 + 2];

            Vector3 v0 = _verts[i0];
            Vector3 v1 = _verts[i1];
            Vector3 v2 = _verts[i2];

            _faceCenters[f] = (v0 + v1 + v2) / 3f;
            _faceNormals[f] = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
        }
    }

    void Update()
    {

        // ---- delay logic ----
        if (!_canHide)
        {
            _timeSinceSpawn += Time.deltaTime;
            if (_timeSinceSpawn >= startDelaySeconds)
                _canHide = true;
            else
                return; // do nothing until delay passes
        }
        
        if (_cam == null) _cam = Camera.main;
        if (_cam == null || _mesh == null) return;

        // Ray from camera through center of view (gaze direction)
        Ray ray = new Ray(_cam.transform.position, _cam.transform.forward);

        // Intersect ray with sphere surface approximately:
        // We'll find the closest face center to the ray hit-point by ray-sphere intersection.
        if (!RaySphere(ray, transform.position, GetApproxWorldRadius(), out Vector3 hitPoint))
            return;

        // Convert hit point to local space to compare with face centers
        Vector3 hitLocal = transform.InverseTransformPoint(hitPoint);

        // Camera direction in local space
        Vector3 camPosLocal = transform.InverseTransformPoint(_cam.transform.position);
        Vector3 viewDirLocal = Vector3.Normalize(hitLocal - camPosLocal);

        float hitRadiusLocal = hitRadiusMeters / Mathf.Max(0.0001f, transform.lossyScale.x);

        int hiddenThisFrame = 0;

        for (int f = 0; f < _hidden.Length; f++)
        {
            if (_hidden[f]) continue;

            // only near the gaze hit area
            if ((_faceCenters[f] - hitLocal).sqrMagnitude > hitRadiusLocal * hitRadiusLocal)
                continue;

            // only faces that face the camera a bit
            float dot = Vector3.Dot(_faceNormals[f], -viewDirLocal);
            if (dot < facingDotThreshold)
                continue;

            HideFace(f);
            hiddenThisFrame++;
            if (hiddenThisFrame >= maxFacesHidePerFrame)
                break;
        }

        if (hiddenThisFrame > 0)
            _mesh.colors = _colors; // push updates
    }

    void HideFace(int f)
    {
        _hidden[f] = true;

        int i0 = _tris[f * 3 + 0];
        int i1 = _tris[f * 3 + 1];
        int i2 = _tris[f * 3 + 2];

        // Set alpha = 0 for the triangle's vertices
        _colors[i0].a = 0f;
        _colors[i1].a = 0f;
        _colors[i2].a = 0f;
    }

    float GetApproxWorldRadius()
    {
        // Sphere mesh is roughly unit radius in local space.
        // If your local sphere radius differs, you can expose a field.
        return 0.5f * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
    }

    static bool RaySphere(Ray ray, Vector3 center, float radius, out Vector3 hitPoint)
    {
        // standard ray-sphere intersection (closest hit in front of ray)
        Vector3 oc = ray.origin - center;
        float b = Vector3.Dot(oc, ray.direction);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float h = b * b - c;

        if (h < 0)
        {
            hitPoint = default;
            return false;
        }

        h = Mathf.Sqrt(h);
        float t = -b - h;
        if (t < 0) t = -b + h;
        if (t < 0)
        {
            hitPoint = default;
            return false;
        }

        hitPoint = ray.origin + ray.direction * t;
        return true;
    }
}
