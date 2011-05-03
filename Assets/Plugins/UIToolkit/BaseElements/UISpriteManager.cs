/*
	Many thanks to the excellent work of Brady Wright from Above and Beyond Software for providing the community with the
	amazing SpriteManager back in the day.  A few bits of his original code are buried somewhere in here.  Be sure to check
	out his amazing products in the Unity Asset Store.
	http://forum.unity3d.com/threads/16763-SpriteManager-draw-lots-of-sprites-in-a-single-draw-call!?highlight=spritemanager
*/
using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;



public struct UITextureInfo
{
	public UIUVRect uvRect;
	public Vector2 size;
	public Rect frame;
}


public class UISpriteManager : MonoBehaviour 
{

	#region ivars
	
	// Which way to wind polygons?
	public enum WINDING_ORDER
	{
	    CCW,        // Counter-clockwise
	    CW      	// Clockwise
	};
	
	// if set to true, the texture will be chosen and loaded from textureName
	// the base texture to use. If HD/retina, textureName2x will be loaded.  Both need to be in Resources.
	// this also doubles as the name of the json config file generated by Texture Packer
	public bool autoTextureSelectionForHD = false;
	public bool allowPod4GenHD = true; // if false, iPod touch 4G will not take part in HD mode
	public string texturePackerConfigName;
	[HideInInspector]
	public bool isHD = false;
	
	public Material material;            // The material to use for the sprites
	public int startSpriteCount = 10;        // How many sprites to allocate space for
	public WINDING_ORDER winding = WINDING_ORDER.CCW; // Which way to wind polygons
	
	protected bool meshIsDirty = false; // Flag that gets set if any of the following flags are set.  No updates will happen unless this is true
	protected bool vertsChanged = false;    // Have changes been made to the vertices of the mesh since the last frame?
	protected bool uvsChanged = false;    // Have changes been made to the UVs of the mesh since the last frame?
	protected bool colorsChanged = false;   // Have the colors changed?
	protected bool vertCountChanged = false;// Has the number of vertices changed?
	protected bool updateBounds = false;    // Update the mesh bounds?
	
	protected UISprite[] sprites;    // Array of all sprites (the offset of the vertices corresponding to each sprite should be found simply by taking the sprite's index * 4 (4 verts per sprite).
	
	protected MeshFilter meshFilter;
	protected MeshRenderer meshRenderer;
	protected Mesh mesh;                    // Reference to our mesh (contained in the MeshFilter)
	[HideInInspector]
	public Vector2 textureSize = Vector2.zero;
	
	protected Vector3[] vertices;  // The vertices of our mesh
	protected int[] triIndices;    // Indices into the vertex array
	protected Vector2[] UVs;       // UV coordinates
	protected Color[] colors;      // Color values
	
	protected Dictionary<string, UITextureInfo> textureDetails; // texture details loaded from the TexturePacker config file

	#endregion;
	

	#region Unity MonoBehaviour Functions
	
    virtual protected void Awake()
    {
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();

        meshRenderer.renderer.material = material;
        mesh = meshFilter.mesh;

        // Create our vert, UV, color and sprite arrays
		createArrays( startSpriteCount );

        // Move the object to the origin so the objects drawn will not be offset from the objects they are intended to represent.
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;

		// handle texture loading if required
		var deviceAllowsHD = ( allowPod4GenHD && iPhoneSettings.generation == iPhoneGeneration.iPodTouch4Gen ) || iPhoneSettings.generation != iPhoneGeneration.iPodTouch4Gen;
		if( autoTextureSelectionForHD && deviceAllowsHD )
		{
			// are we laoding up a 2x texture?
#if UNITY_EDITOR
			if( Screen.width >= 500 || Screen.height >= 500 ) // for easier testing in the editor
#else
			if( Screen.width >= 960 || Screen.height >= 960 )
#endif
			{
#if UNITY_EDITOR
				Debug.Log( "switching to 2x GUI texture" );
#endif
				texturePackerConfigName = texturePackerConfigName + "2x";
				isHD = true;
			}
		}

		// load our texture		
		var texture = (Texture)Resources.Load( texturePackerConfigName, typeof( Texture ) );
		if( texture == null )
			Debug.Log( "UI texture is being autoloaded and it doesnt exist: " + texturePackerConfigName );
		material.SetTexture( "_MainTex", texture );
		
		// Cache our texture size
		Texture t = material.GetTexture( "_MainTex" );
		textureSize = new Vector2( t.width, t.height );
		
		// load up the config file
		textureDetails = loadTexturesFromTexturePackerJSON( texturePackerConfigName + ".json" );
    }

	
	// Performs any changes if the verts, UVs, colors or bounds changed
	protected void updateMeshProperties()
	{
        // Were changes made to the mesh since last time?
        if( vertCountChanged )
        {
            vertCountChanged = false;
            colorsChanged = false;
            vertsChanged = false;
            uvsChanged = false;
            updateBounds = false;

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = UVs;
            mesh.colors = colors;
            mesh.triangles = triIndices;
        }
        else
        {
            if( vertsChanged )
            {
                vertsChanged = false;
                updateBounds = true;

                mesh.vertices = vertices;
            }

            if( updateBounds )
            {
                mesh.RecalculateBounds();
                updateBounds = false;
            }

            if( colorsChanged )
            {
                colorsChanged = false;
                mesh.colors = colors;
            }

            if( uvsChanged )
            {
                uvsChanged = false;
                mesh.uv = UVs;
            }
        } // end else
	}
	
	#endregion;
	
	
	#region Texture management
	
	private Dictionary<string, UITextureInfo> loadTexturesFromTexturePackerJSON( string filename )
	{
		var textures = new Dictionary<string, UITextureInfo>();
		
		string jsonConfigFile = Application.dataPath;
	
#if UNITY_EDITOR
		jsonConfigFile = jsonConfigFile.Substring( 0, jsonConfigFile.Length ) + "/StreamingAssets/" + filename;
		
		// sanity check while in the editor
		if( !System.IO.File.Exists( jsonConfigFile ) )
			throw new Exception( "texture packer config file doesnt exist: " + jsonConfigFile );
#else
		jsonConfigFile = jsonConfigFile + "/Raw/" + filename;
#endif
	
		using( var sr = new System.IO.StreamReader( jsonConfigFile ) )
		{
			var jsonString = sr.ReadToEnd();
			var decodedHash = (Hashtable)MiniJSON.JsonDecode( jsonString );
			var frames = (Hashtable)decodedHash["frames"];
		
			foreach( System.Collections.DictionaryEntry item in frames )
			{
				// extract the info we need from the TexturePacker json file.  mainly uvRect and size
				var sourceSize = (Hashtable)((Hashtable)item.Value)["sourceSize"];
				var frame = (Hashtable)((Hashtable)item.Value)["frame"];
				var frameX = int.Parse( frame["x"].ToString() );
				var frameY = int.Parse( frame["y"].ToString() );
				var frameW = int.Parse( frame["w"].ToString() );
				var frameH = int.Parse( frame["h"].ToString() );
			
				var ti = new UITextureInfo();
				ti.frame = new Rect( frameX, frameY, frameW, frameH );
				ti.size = new Vector2( float.Parse( sourceSize["w"].ToString() ), float.Parse( sourceSize["h"].ToString() ) );
				ti.uvRect = new UIUVRect( frameX, frameY, frameW, frameH );
			
				textures.Add( item.Key.ToString(), ti );
			}
		}
		
		return textures;
	}
	
	
	// grabs the UITextureInfo for the given filename
	public UITextureInfo textureInfoForFilename( string filename )
	{
#if UNITY_EDITOR
		// sanity check while in editor
		if( !textureDetails.ContainsKey( filename ) )
			throw new Exception( "can't find texture details for texture packer sprite:" + filename );
#endif
		return textureDetails[filename];
	}
	
	
	// grabs the uvRect for the given filename
	public UIUVRect uvRectForFilename( string filename )
	{
#if UNITY_EDITOR
		// sanity check while in editor
		if( !textureDetails.ContainsKey( filename ) )
			throw new Exception( "can't find texture details for texture packer sprite:" + filename );
#endif
		return textureDetails[filename].uvRect;
	}
		
		
	// grabs the frame for the given filename
	public Rect frameForFilename( string filename )
	{
#if UNITY_EDITOR
		// sanity check while in editor
		if( !textureDetails.ContainsKey( filename ) )
			throw new Exception( "can't find texture details for texture packer sprite:" + filename );
#endif
		return textureDetails[filename].frame;
	}
	
	#endregion
	
	
	#region Vertex and UV array management
	
	// Initializes all required arrays
    protected void createArrays( int count )
    {
        // Create the sprite array
        sprites = new UISprite[count];

        // Vertices:
        vertices = new Vector3[count * 4];
        
        // UVs:
        UVs = new Vector2[count * 4];

        // Colors:
        colors = new Color[count * 4];

        // Triangle indices:
        triIndices = new int[count * 6];

        // Inform existing sprites of the new vertex and UV buffers:
        //for( int i = 0; i < firstNewElement; ++i )
        //    sprites[i].setBuffers( vertices, UVs );

        // Setup the triIndices
        for( int i = 0; i < sprites.Length; ++i )
        {
            // Init triangle indices:
            if( winding == WINDING_ORDER.CCW ) // Counter-clockwise winding
            {
                triIndices[i * 6 + 0] = i * 4 + 0;  //    0_ 2            0 ___ 3
                triIndices[i * 6 + 1] = i * 4 + 1;  //  | /      Verts:  |   /|
                triIndices[i * 6 + 2] = i * 4 + 3;  // 1|/                1|/__|2

                triIndices[i * 6 + 3] = i * 4 + 3;  //      3
                triIndices[i * 6 + 4] = i * 4 + 1;  //   /|
                triIndices[i * 6 + 5] = i * 4 + 2;  // 4/_|5
            }
            else
            {   // Clockwise winding
                triIndices[i * 6 + 0] = i * 4 + 0;  //    0_ 1            0 ___ 3
                triIndices[i * 6 + 1] = i * 4 + 3;  //  | /      Verts:  |   /|
                triIndices[i * 6 + 2] = i * 4 + 1;  // 2|/                1|/__|2

                triIndices[i * 6 + 3] = i * 4 + 3;  //      3
                triIndices[i * 6 + 4] = i * 4 + 2;  //   /|
                triIndices[i * 6 + 5] = i * 4 + 1;  // 5/_|4
            }
        }

        vertsChanged = true;
        uvsChanged = true;
        colorsChanged = true;
        vertCountChanged = true;
		meshIsDirty = true;
    }
	
	
    // Enlarges the sprite array by the specified count and also resizes the UV and vertex arrays by the necessary corresponding amount.
    // Returns the index of the first newly allocated element
    protected int enlargeArrays( int count )
    {
        int firstNewElement = sprites.Length;

        // Resize sprite array:
        UISprite[] tempSprites = sprites;
        sprites = new UISprite[sprites.Length + count];
        tempSprites.CopyTo( sprites, 0 );

        // Vertices:
        Vector3[] tempVerts = vertices;
        vertices = new Vector3[vertices.Length + count * 4];
        tempVerts.CopyTo( vertices, 0 );
        
        // UVs:
        Vector2[] tempUVs = UVs;
        UVs = new Vector2[UVs.Length + count * 4];
        tempUVs.CopyTo( UVs, 0 );

        // Colors:
        Color[] tempColors = colors;
        colors = new Color[colors.Length + count * 4];
        tempColors.CopyTo( colors, 0 );

        // Triangle indices:
        int[] tempTris = triIndices;
        triIndices = new int[triIndices.Length + count * 6];
        tempTris.CopyTo( triIndices, 0 );

        // Inform existing sprites of the new vertex and UV buffers:
        for( int i = 0; i < firstNewElement; ++i )
            sprites[i].setBuffers( vertices, UVs );

        // Setup the newly-added sprites and Add them to the list of available 
        // sprite blocks. Also initialize the triangle indices while we're at it:
        for( int i = firstNewElement; i < sprites.Length; ++i )
        {
            // Init triangle indices:
            if( winding == WINDING_ORDER.CCW )
            {   // Counter-clockwise winding
                triIndices[i * 6 + 0] = i * 4 + 0;  //    0_ 2            0 ___ 3
                triIndices[i * 6 + 1] = i * 4 + 1;  //  | /      Verts:  |   /|
                triIndices[i * 6 + 2] = i * 4 + 3;  // 1|/                1|/__|2

                triIndices[i * 6 + 3] = i * 4 + 3;  //      3
                triIndices[i * 6 + 4] = i * 4 + 1;  //   /|
                triIndices[i * 6 + 5] = i * 4 + 2;  // 4/_|5
            }
            else
            {   // Clockwise winding
                triIndices[i * 6 + 0] = i * 4 + 0;  //    0_ 1            0 ___ 3
                triIndices[i * 6 + 1] = i * 4 + 3;  //  | /      Verts:  |   /|
                triIndices[i * 6 + 2] = i * 4 + 1;  // 2|/                1|/__|2

                triIndices[i * 6 + 3] = i * 4 + 3;  //      3
                triIndices[i * 6 + 4] = i * 4 + 2;  //   /|
                triIndices[i * 6 + 5] = i * 4 + 1;  // 5/_|4
            }
        }

        vertsChanged = true;
        uvsChanged = true;
        colorsChanged = true;
        vertCountChanged = true;
		meshIsDirty = true;

        return firstNewElement;
    }
	
	#endregion
	

	#region Add/Remove sprite functions

    public UISprite addSprite( string name, int xPos, int yPos, int depth = 1, bool gameObjectOriginInCenter = false )
    {
#if UNITY_EDITOR
		// sanity check while in editor
		if( !textureDetails.ContainsKey( name ) )
			throw new Exception( "can't find texture details for texture packer sprite:" + name );
#endif
		var textureInfo = textureDetails[name];
		var positionRect = new Rect( xPos, yPos, textureInfo.size.x, textureInfo.size.y );

		return this.addSprite( positionRect, textureInfo.uvRect, depth, gameObjectOriginInCenter );
    }

	
	// shortcut for adding a new sprite using the raw materials
    private UISprite addSprite( Rect frame, UIUVRect uvFrame, int depth, bool gameObjectOriginInCenter = false )
    {
        // Create and initialize the new sprite
		UISprite newSprite = new UISprite( frame, depth, uvFrame, gameObjectOriginInCenter );
		addSprite( newSprite );
		
		return newSprite;
    }


    // Adds a sprite to the manager
    public void addSprite( UISprite sprite )
    {
        // Initialize the new sprite and update the UVs		
		int i = 0;
	
		// Find the first available sprite index
		for( ; i < sprites.Length; i++ )
		{
			if( sprites[i] == null )
				break;
		}
		
		// did we find a sprite?  if not, expand our arrays
		if( i == sprites.Length )
			i = enlargeArrays( 5 );
		
        // Assign and setup the sprite
		sprites[i] = sprite;
		
        sprite.index = i;
        sprite.manager = this;

        sprite.setBuffers( vertices, UVs );

		// Setup indices of the sprites vertices, UV entries and color values
		sprite.vertexIndices.initializeVertsWithIndex( i );
		sprite.initializeSize();
		
		sprite.color = Color.white;
		
        // Set our flags:
        vertsChanged = true;
        uvsChanged = true;
		meshIsDirty = true;
    }


    protected void removeSprite( UISprite sprite )
    {
		vertices[sprite.vertexIndices.mv.one] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.two] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.three] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.four] = Vector3.zero;

        sprites[sprite.index] = null;
		
		// This should happen when the sprite dies!!
		//Destroy( sprite.client );
		
        vertsChanged = true;
		meshIsDirty = true;
    }

	#endregion;
	
	
	#region Show/Hide sprite functions

    public void hideSprite( UISprite sprite )
    {
        sprite.___hidden = true;

		vertices[sprite.vertexIndices.mv.one] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.two] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.three] = Vector3.zero;
		vertices[sprite.vertexIndices.mv.four] = Vector3.zero;

        vertsChanged = true;
		meshIsDirty = true;
    }

	
    public void showSprite( UISprite sprite )
    {
        if( !sprite.___hidden )
            return;

        sprite.___hidden = false;

        // Update the vertices.  This will end up caling UpdatePositions() to set the vertsChanged flag
        sprite.updateTransform();
    }

	#endregion;
	

	#region Update UV, colors and positions
	
    // Updates the UVs of the specified sprite and copies the new values into the mesh object.
    public void updateUV( UISprite sprite )
    {
		UVs[sprite.vertexIndices.uv.one] = sprite.uvFrame.lowerLeftUV + Vector2.up * sprite.uvFrame.uvDimensions.y;  // Upper-left
		UVs[sprite.vertexIndices.uv.two] = sprite.uvFrame.lowerLeftUV;                              // Lower-left
		UVs[sprite.vertexIndices.uv.three] = sprite.uvFrame.lowerLeftUV + Vector2.right * sprite.uvFrame.uvDimensions.x;// Lower-right
		UVs[sprite.vertexIndices.uv.four] = sprite.uvFrame.lowerLeftUV + sprite.uvFrame.uvDimensions;     // Upper-right
        
        uvsChanged = true;
		meshIsDirty = true;
    }
	

    // Updates the color values of the specified sprite and copies the new values into the mesh object.
    public void updateColors( UISprite sprite )
    {
		colors[sprite.vertexIndices.cv.one] = sprite.color;
		colors[sprite.vertexIndices.cv.two] = sprite.color;
		colors[sprite.vertexIndices.cv.three] = sprite.color;
		colors[sprite.vertexIndices.cv.four] = sprite.color;

        colorsChanged = true;
		meshIsDirty = true;
    }

	
    // Informs the SpriteManager that some vertices have changed position and the mesh needs to be reconstructed accordingly
    public void updatePositions()
    {
        vertsChanged = true;
		meshIsDirty = true;
    }

	#endregion;

}
