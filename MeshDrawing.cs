using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace NetworkInteractable
{
    public class MeshDrawing : NetworkBehaviour
    {       
        private const int k_MaxBresenhamCoord = 1000;
        private const string k_WritableMatName = "WritableMat";

        [SerializeField] LayerMask m_DrawableLayerMask;

        [Header("Material")]
        [SerializeField] int m_WritingTexRes = 2048;
        [SerializeField] Material m_WritableMat;

        [Header("Brush")]
        [Range(1, 40)] public short m_defaultBrushRadius = 10;
        public Color m_defaultBrushColour = Color.black;
        public Color m_drawTextureTint = Color.black;
        public Texture2D m_drawTexture;

        [Tooltip("Percentage to scale drawn texture")]
        [Range(.1f, 10f)] public float m_drawTextureScale = 1f;

        [Tooltip("Alpha threshold for texture pixel to be drawn - Should be more than 0 for any alpha textures to save performance")]
        [Range(0, 1)]
        public float m_alphaThreshold = .1f;
        public bool m_useDrawTexture = false;
        public bool m_useBresnhamAlgorithm = false;

        [Tooltip("Interval between points that are used in bresenham algorithm - Higher values will be less accurate but save on speed")]
        [Range(0, 10)]
        public int m_bresenhamInterval = 3;

        public PixelCombinationType PixelCombineType;

        [Tooltip("Ratio to blend pixels when using blend mode")]
        [Range(0, 1)] public float m_blendAmount = .4f;

        [Header("Network")]
        [Tooltip("Maximum number of points that are collected before being sent over the network")]
        [Range(10, 2500)]
        public int m_maxNetworkArraySize = 1000;

        [Tooltip("Speed of drawing other clients data")]
        [Range(1, 5)]
        public short m_clientDrawSpeed = 1;

        [Tooltip("Delay in seconds between streaming new texture data to the GC")]
        public float m_changeDelay = .01f;
        public float m_maxDrawDistance = 40.0f;

        [Tooltip("Time between sending updates on the network (Deprecated)")]
        public float m_networkDelay = 0.4f;
      
        Vector2Short m_pixelCoord = Vector2Short.zero;
        Vector2Short m_prevLocalCoord = Vector2Short.zero;
        Vector2Short m_previousMousePos = Vector2Short.zero;
        Vector2Short m_mousePos = Vector2Short.zero;

        // local
        bool m_localChangeMade = false;
        // network
        bool m_networkChangeMade = false;

        float m_delayTimer = 0f;
        float m_queueTexTimer = 0f;

        Camera m_playerCam;
        Texture2D m_localTex;
        GameObject m_selectedObject;
        Renderer m_selectObjRend;
        Material m_writableMaterial;

        List<Vector2Short> m_networkCoordinates = new List<Vector2Short>(1000);

        // bresenham coordinate array
        Vector2Short[] m_bresenhamList = new Vector2Short[k_MaxBresenhamCoord];
        private int m_bresenhamCounter = 0;

        // Color array of the currently selected draw texture
        List<Color> m_drawTexColMap = new List<Color>();       

        private delegate void PixelCombination(Color pixel1, Color pixel2, out Color finalPixel);
        private PixelCombination pixelCombineFunc;

        public enum PixelCombinationType
        {
            Add,
            Blend,
            Multiply,
            Subtract,
            Darken,
            Lighten,
            Screen,
        }

        // Start is called before the first frame update
        void Start()
        {
            setupWritableObjects();
            changeDrawTexture(m_drawTexture);
            setPixelCombinationType(PixelCombineType);

            for (int i = 0; i < m_bresenhamList.Length; i++)
            {
                m_bresenhamList[i] = new Vector2Short();
            }

            m_playerCam = FirstPersonController.PlayerCam;
        }

        // Update is called once per frame
        void Update()
        {
            if (!isLocalPlayer)
                return;

            if (Input.GetKeyDown(KeyCode.C))
            {
                ClearTextureAlpha(m_localTex);
            }

            m_mousePos.Set((short)Input.mousePosition.x, (short)Input.mousePosition.y);

            if (Input.GetMouseButton(0))
            {
                RaycastHit hit;
                if (Physics.Raycast(m_playerCam.transform.position, m_playerCam.transform.forward, out hit, m_maxDrawDistance, m_DrawableLayerMask))
                {
                    GameObject hitObj = hit.collider.gameObject;

                    m_localChangeMade = m_networkChangeMade = true;                     

                    // when changing object grab data for drawing on that object
                    if (m_selectedObject != null && m_selectedObject != hitObj)
                    {
                        changeDefaultValues(hitObj);
                        Debug.Log("Select new object");
                    }
                    else if (m_selectedObject == null)
                    {
                        changeDefaultValues(hitObj);
                        Debug.Log("Selected first object");
                    }

                    if (m_writableMaterial && m_localTex)
                    {
                        // find pixel coordinate to affect
                        Vector2 texCoord = hit.textureCoord;

                        m_pixelCoord.x = (short)(m_localTex.width * texCoord.x);
                        m_pixelCoord.y = (short)(m_localTex.height * texCoord.y);

                        // grab previous coordinate if available
                        if (m_networkCoordinates.Count > 1)
                            m_prevLocalCoord = m_networkCoordinates[m_networkCoordinates.Count - 1];

                        // reached maximum array size, so send current values
                        if (m_networkCoordinates.Count > m_maxNetworkArraySize)
                        {
                            CmdDrawLines(m_networkCoordinates.ToArray(), m_selectedObject, m_clientDrawSpeed, m_changeDelay, m_defaultBrushRadius);
                            resetLocalValues();
                        }

                        // add coordinate to list if position is different than last coordinate
                        if (m_pixelCoord != m_prevLocalCoord)
                        {
                            m_networkCoordinates.Add(m_pixelCoord);

                            // draw locally
                            drawPoints(m_pixelCoord, m_prevLocalCoord, m_localTex, m_defaultBrushRadius, m_networkCoordinates.Count == 1);
                        }
                    }
                }
            }
            // Release input so send network data to clients immediately
            else if (Input.GetMouseButtonUp(0) && m_networkCoordinates.Count > 0)
            {
                CmdDrawLines(m_networkCoordinates.ToArray(), m_selectedObject, m_clientDrawSpeed, m_changeDelay, m_defaultBrushRadius);
                m_previousMousePos = Vector2Short.zero;
                m_prevLocalCoord = Vector2Short.zero;

                resetLocalValues();
            }

            // chek locally for applying texture changes
            CheckApplyTexChanges(m_localTex);
        }

        private void LateUpdate()
        {
            // grab mouse pos for previous position calculations
            m_previousMousePos.Set((short)Input.mousePosition.x, (short)Input.mousePosition.y);
            m_bresenhamCounter = 0;
        }

        private void CheckApplyTexChanges(Texture2D texture)
        {
            // apply new local changes
            if (m_localChangeMade)
            {
                m_delayTimer += Time.deltaTime;
                if (m_delayTimer > m_changeDelay)
                {
                    ApplyTextureChanges(texture);

                    m_localChangeMade = false;
                    m_delayTimer = 0f;
                }
            }
        }


        /// <summary>
        /// Apply a new texture for drawing
        /// </summary>
        /// <param name="newTex"></param>
        private void changeDrawTexture(Texture2D newTex)
        {
            // fill list with draw texture data
            if (newTex)
            {
                m_drawTexColMap.Capacity = newTex.width * newTex.height;
                Color[] colArray = newTex.GetPixels(0);

                int colArrayLength = colArray.Length;
                for (int i = 0; i < colArrayLength; i++)
                {
                    m_drawTexColMap.Add(colArray[i]);
                }
            }
        }

        /// <summary>
        /// Sends updated texture to the GC
        /// </summary>
        /// <param name="texture"></param>
        /// <returns></returns>
        private bool ApplyTextureChanges(Texture2D texture)
        {
            if (texture)
            {
                texture.Apply();
                return true;
            }
            else
            {
                Debug.LogWarning("Failed to apply texture changes", texture);
                return false;
            }
        }

        /// <summary>
        /// Server command to send coordinate data to clients
        /// </summary>
        /// <param name="coordinates"></param>
        /// <param name="go"></param>
        /// <param name="drawSpeed"></param>
        /// <param name="applyDelay"></param>
        /// <param name="brushSize"></param>
        [Command]
        private void CmdDrawLines(Vector2Short[] coordinates, GameObject go, short drawSpeed, float applyDelay, short brushSize)
        {
            bool clear = true;

            Debug.Log("GO passed through to client draw - " + go);

            if (m_networkCoordinates.Count < 0)
            {
                Debug.LogWarning("Attempted to send client data without coordinate points");
                clear = false;
            }
            if (!go)
            {
                Debug.LogWarning("Attempted to send client data without object to affect", go);
                clear = false;
            }

            if (clear)
                RpcStartDrawCoroutine(coordinates, go, drawSpeed, applyDelay, brushSize);
        }

        /// <summary>
        /// Client function to recieve data from other clients for drawing on textures
        /// </summary>
        /// <param name="coordinates"></param>
        /// <param name="go"></param>
        /// <param name="drawDelay"></param>
        /// <param name="applyDelay"></param>
        /// <param name="brushSize"></param>
        [ClientRpc]
        private void RpcStartDrawCoroutine(Vector2Short[] coordinates, GameObject go, short drawSpeed, float applyDelay, short brushSize)
        {
            if (isLocalPlayer)
                return;

            StartCoroutine(drawOtherClientLines(coordinates, go, drawSpeed, applyDelay, brushSize));
        }


        // call on clients to initiate drawing of other clients data
        private IEnumerator drawOtherClientLines(Vector2Short[] coordinates, GameObject go, short drawSpeed, float applyDelay, short brushSize)
        {
            if (!go)
            {
                Debug.LogWarning("GO on client network draw was null - check to see if it has a network transform", go);
                yield break;
            }

            var rend = go.GetComponent<Renderer>();
            Texture2D texture = null;
            Material writeMat = null;

            if (rend)
            {
                writeMat = getWritableMat(rend);
                if (writeMat)
                    texture = (Texture2D)writeMat.mainTexture;
                else
                    Debug.LogWarning("Failed to find writable mat on other client sent object");
            }

            if (!texture)
            {
                Debug.LogWarning("Failed to grab texture to affect on client draw", texture);
                yield break;
            }

            Vector2Short prevCoord = Vector2Short.zero;
            int coordCount = coordinates.Length;
            float applyTimer = 0f;
            int index = 0;

            while (index < coordCount)
            {
                applyTimer += Time.deltaTime;
                int updatedDrawSpeed = (int)(drawSpeed * (Time.deltaTime * 200.0f));

                // Draw loop
                for (int i = 0; i < updatedDrawSpeed; i++, index++)
                {
                    // breakout early at the end
                    if (index >= coordCount)
                        break;

                    if (index < coordCount)
                    {
                        // grab previous point
                        if (index > 0)
                            prevCoord = coordinates[index - 1];

                        drawPoints(coordinates[index], prevCoord, texture, brushSize, index == 0);
                    }
                }

                if (applyTimer >= applyDelay)
                {
                    ApplyTextureChanges(texture);
                    applyTimer = 0f;
                }

                // wait till next frame
                yield return null;
            }

            // apply final changes before exiting
            ApplyTextureChanges(texture);
        }

        /// <summary>
        /// Draws a point with the brush on the designated texture, along with the bresenham line algorithm to fill the connecting pixels
        /// </summary>
        /// <param name="coordinate"></param>
        /// <param name="prevCoord"></param>
        /// <param name="texture"></param>
        /// <param name="brushSize"></param>
        private void drawPoints(Vector2Short coordinate, Vector2Short prevCoord, Texture2D texture, int brushSize, bool firstPoint)
        {
            if (firstPoint)
                return;

            if (texture && prevCoord != Vector2Short.zero)
            {               
                drawBrush(texture, brushSize, coordinate, prevCoord, true);

                // bresenhams algorithm
                if (m_useBresnhamAlgorithm)
                {
                    // calculate connecting points
                    bresenhamsAlgorithm(firstPoint ? coordinate : prevCoord, coordinate);
                    drawBresenhamAlgorithm(texture, brushSize, true);
                }
            }
        }

        /// <summary>
        /// Draws points on texture using bresenhamAlgorithm to connect the last two coordinates
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="brushSize"></param>
        /// <param name="circular"></param>
        private void drawBresenhamAlgorithm(Texture2D texture, int brushSize, bool circular)
        {
            if (m_drawTexture && m_useDrawTexture)
            {
                if (pixelCombineFunc == null)
                {
                    Debug.LogError("PixelCombineFunction hasn't been set in drawBrush call");
                    return;
                }

                Vector2 dir = new Vector2();
                Vector2Short prevCoord = m_bresenhamList[0];

                Color basePixel;
                Color drawPixel;
                Color finalPixel = Color.black;

                int drawTextureWidth = m_drawTexture.width;
                int drawTextureHeight = m_drawTexture.height;
                int texHeight = texture.height;
                int texWidth = texture.width;

                for (int i = 0; i < m_bresenhamCounter; i++)
                {                    
                    var coordinate = m_bresenhamList[i];
              
                    // direction vector
                    Vector2Short shortDir = coordinate - prevCoord;
                    prevCoord = coordinate;

                    dir.Set(shortDir.x, shortDir.y);
                    Vector2 perp = Vector2.zero;
                    Vector2 pos;

                    perp.Set(dir.y, -dir.x);
                    dir.Normalize();
                    perp.Normalize();

                    short index = 0;

                    if (m_useDrawTexture)
                    {
                        for (int y = 0; y < drawTextureHeight; y++)
                        {
                            for (int x = 0; x < drawTextureWidth; x++)
                            {
                                drawPixel = m_drawTexColMap[index];
                                if (drawPixel.a > m_alphaThreshold)
                                {
                                    // apply position and rotation on pixel grid
                                    pos = coordinate + (((x - (drawTextureWidth / 2)) * m_drawTextureScale) * dir) + (((y - (drawTextureHeight / 2)) * m_drawTextureScale) * perp);

                                    // prevent wrap around
                                    if (pos.x <= texWidth && pos.y <= texHeight && pos.x > 0 && pos.y > 0)
                                    {
                                        basePixel = texture.GetPixel((int)pos.x, (int)pos.y);
                                        pixelCombineFunc(basePixel, drawPixel + m_drawTextureTint, out finalPixel);
                                        texture.SetPixel((int)pos.x, (int)pos.y, finalPixel);
                                    }
                                }

                                index++;
                            }
                        }
                    }
                    // Uses simple non-texture brush
                    else
                    {
                        for (int y = -brushSize; y < brushSize; ++y)
                        {
                            for (int x = -brushSize; x < brushSize; ++x)
                            {
                                // circular brush
                                if (circular)
                                {
                                    if ((y * y + x * x) < (brushSize * brushSize))
                                    {
                                        texture.SetPixel(coordinate.x + y, coordinate.y + x, m_defaultBrushColour);
                                    }
                                }
                                // square brush
                                else
                                    texture.SetPixel(coordinate.x + y, coordinate.y + x, m_defaultBrushColour);
                            }
                        }
                    }
                }                         
            }         
        }

        /// <summary>
        /// Draws point on texture either with texture or simple brush
        /// </summary>
        /// <param name="texture"></param>
        /// <param name="brushSize"></param>
        /// <param name="coordinate"></param>
        /// <param name="prevCoord"></param>
        /// <param name="circular"></param>
        private void drawBrush(Texture2D texture, int brushSize, Vector2Short coordinate, Vector2Short prevCoord, bool circular)
        {
            if (m_drawTexture && m_useDrawTexture)
            {
                if (pixelCombineFunc == null)
                {
                    Debug.LogError("PixelCombineFunction hasn't been set in drawBrush call");
                    return;
                }

                Vector2Short shortDir = coordinate - prevCoord;

                Vector2 dir = new Vector2(shortDir.x, shortDir.y);
                Vector2 perp = Vector2.zero;
                Vector2 pos;

                perp.Set(dir.y, -dir.x);
                dir.Normalize();
                perp.Normalize();

                int drawTextureWidth = m_drawTexture.width;
                int drawTextureHeight = m_drawTexture.height;

                short index = 0;
                Color basePixel;
                Color drawPixel;
                Color finalPixel = Color.black;

                for (int y = 0; y < drawTextureHeight; y++)
                {
                    for (int x = 0; x < drawTextureWidth; x++)
                    {
                        drawPixel = m_drawTexColMap[index];
                        if (drawPixel.a > m_alphaThreshold)
                        {
                            // apply position and rotation on pixel grid
                            pos = coordinate + (((x - (drawTextureWidth / 2)) * m_drawTextureScale) * dir) + (((y - (drawTextureHeight / 2)) * m_drawTextureScale) * perp);

                            // prevent wrap around
                            if (pos.x <= texture.width && pos.y <= texture.height && pos.x > 0 && pos.y > 0)
                            {
                                basePixel = texture.GetPixel((int)pos.x, (int)pos.y);
                                pixelCombineFunc(basePixel, drawPixel + m_drawTextureTint, out finalPixel);
                                texture.SetPixel((int)pos.x, (int)pos.y, finalPixel);
                            }
                        }

                        index++;
                    }
                }           
            }
            // Uses simple non-texture brush
            else
            {
                for (int y = -brushSize; y < brushSize; ++y)
                {
                    for (int x = -brushSize; x < brushSize; ++x)
                    {
                        // circular brush
                        if (circular)
                        {
                            if ((y * y + x * x) < (brushSize * brushSize))
                            {
                                texture.SetPixel(coordinate.x + y, coordinate.y + x, m_defaultBrushColour);
                            }
                        }
                        // square brush
                        else
                            texture.SetPixel(coordinate.x + y, coordinate.y + x, m_defaultBrushColour);
                    }
                }
            }
        }

        /// <summary>
        /// Calculates list of pixel coordinates between two points to form a connected line
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        private void bresenhamsAlgorithm(Vector2Short start, Vector2Short end)
        {
            int dx = Mathf.Abs(end.x - start.x), sx = start.x < end.x ? 1 : -1;
            int dy = -Mathf.Abs(end.y - start.y), sy = start.y < end.y ? 1 : -1;
            int err = dx + dy, e2; /* error value e_xy */

            int counter = 0;
            for (; ; )
            {  /* loop */
                if (start.x == end.x && start.y == end.y) break;
                e2 = 2 * err;
                if (e2 >= dy) { err += dy; start.x += (short)sx; }
                if (e2 <= dx) { err += dx; start.y += (short)sy; }

                if (m_bresenhamInterval == 0 || counter > m_bresenhamInterval)
                {
                    m_bresenhamList[m_bresenhamCounter].Set(start.x, start.y);
                    m_bresenhamCounter++;
                    counter = 0;
                }
                else
                    counter++;
            }         
        }

        private void resetLocalValues()
        {
            // reset values for timers and network values
            m_networkCoordinates.Clear();
            m_networkChangeMade = false;
        }

        /// <summary>
        /// Change default values for local drawing when selecing drawing mesh, to ensure values will be kept while drawing on the same mesh
        /// </summary>
        /// <param name="hitObj"></param>
        private void changeDefaultValues(GameObject hitObj)
        {
            m_prevLocalCoord = Vector2Short.zero;

            // if we already have object we were affecting then send message to start drawing on clients
            if (m_selectedObject)
            {
                CmdDrawLines(m_networkCoordinates.ToArray(), m_selectedObject, m_clientDrawSpeed, m_changeDelay, m_defaultBrushRadius);
                resetLocalValues();
            }

            m_selectedObject = hitObj;
            m_selectObjRend = m_selectedObject.GetComponent<Renderer>();

            m_writableMaterial = getWritableMat(m_selectObjRend);
            if (m_writableMaterial)
            {
                m_localTex = (Texture2D)m_writableMaterial.mainTexture;
            }
        }


        /// <summary>
        /// Returns writable mat if found on renderer
        /// </summary>
        /// <param name="rend"></param>
        /// <returns></returns>
        private Material getWritableMat(Renderer rend)
        {
            Material writeMaterial = null;

            foreach (var mat in rend.materials)
            {
                if (mat.name == k_WritableMatName + " (Instance)")
                    writeMaterial = mat;
            }

            if (!writeMaterial)
                Debug.LogWarning("Failed to find writableMat", rend);

            return writeMaterial;
        }

        /// <summary>
        /// Add drawable material to writable objects for drawing on
        /// </summary>
        private void setupWritableObjects()
        {
            Texture2D tex;
            Material writableMat;
            List<Material> newMaterials = new List<Material>();

            var writableGOs = GameObject.FindGameObjectsWithTag("Writable");

            foreach (var go in writableGOs)
            {
                if (!go)
                    continue;

                var rend = go.GetComponent<Renderer>();
                if (rend)
                {
                    var materials = rend.materials;
                    newMaterials.AddRange(materials);

                    writableMat = new Material(m_WritableMat);
                    writableMat.name = k_WritableMatName;

                    // create new blank texture with designated dimensions
                    tex = new Texture2D(m_WritingTexRes, m_WritingTexRes, TextureFormat.RGBA32, false);

                    // clear texture
                    ClearTextureAlpha(tex);

                    writableMat.mainTexture = tex;

                    newMaterials.Add(writableMat);
                    rend.materials = newMaterials.ToArray();

                    newMaterials.Clear();
                }
            }
        }

        /// <summary>
        /// Fills texture with alpha pixels
        /// </summary>
        /// <param name="texture"></param>
        public static void ClearTextureAlpha(Texture2D texture)
        {
            Color[] newColors = new Color[texture.width * texture.height];
            for (int i = 0; i < newColors.Length; i++)
            {
                newColors[i] = Color.clear;
            }

            texture.SetPixels(newColors);
            texture.Apply();
        }

        /// <summary>
        /// Set brush pixel combination type based on enum type
        /// </summary>
        /// <param name="type"></param>
        private void setPixelCombinationType(PixelCombinationType type)
        {
            switch (type)
            {
                case PixelCombinationType.Add: { pixelCombineFunc = AddPixel; break; }
                case PixelCombinationType.Blend: { pixelCombineFunc = BlendPixel; break; }
                case PixelCombinationType.Multiply: { pixelCombineFunc = MultiplyPixel; break; }
                case PixelCombinationType.Subtract: { pixelCombineFunc = SubtractPixel; break; }
                case PixelCombinationType.Darken: { pixelCombineFunc = DarkenOnlyPixel; break; }
                case PixelCombinationType.Lighten: { pixelCombineFunc = LightenOnlyPixel; break; }
                case PixelCombinationType.Screen: { pixelCombineFunc = ScreenPixel; break; }
                default: { Debug.LogError("SetPixelCombination passed unrecognised type"); break; }
            }
        }

        private void BlendPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = Color.black;

            finalPixel.r = pixel1.r * (1 - m_blendAmount) + pixel2.r * m_blendAmount;
            finalPixel.g = pixel1.g * (1 - m_blendAmount) + pixel2.g * m_blendAmount;
            finalPixel.b = pixel1.b * (1 - m_blendAmount) + pixel2.b * m_blendAmount;
        }

        private void AddPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = pixel1 + pixel2;
        }

        private void MultiplyPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = pixel1 * pixel2;
        }

        private void SubtractPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = pixel1 - pixel2;
        }

        private void DarkenOnlyPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = Color.black;

            finalPixel.r = Mathf.Min(pixel1.r, pixel2.r);
            finalPixel.g = Mathf.Min(pixel1.g, pixel2.g);
            finalPixel.b = Mathf.Min(pixel1.b, pixel2.b);
        }

        private void LightenOnlyPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = Color.black;

            finalPixel.r = Mathf.Max(pixel1.r, pixel2.r);
            finalPixel.g = Mathf.Max(pixel1.g, pixel2.g);
            finalPixel.b = Mathf.Max(pixel1.b, pixel2.b);
        }

        private void ScreenPixel(Color pixel1, Color pixel2, out Color finalPixel)
        {
            finalPixel = Color.black;

            finalPixel.r = (1 - (1 - pixel1.r) * (1 - pixel2.r));
            finalPixel.g = (1 - (1 - pixel1.g) * (1 - pixel2.g));
            finalPixel.b = (1 - (1 - pixel1.b) * (1 - pixel2.b));
        }
    }
}