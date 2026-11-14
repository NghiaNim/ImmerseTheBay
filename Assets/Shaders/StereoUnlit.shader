// Unlit shader that shows a different half of the texture to each eye.
// _Layout: 0 = mono, 1 = side-by-side (left eye = left half), 2 = top-bottom (left eye = top half).
Shader "ImmerseTheBay/StereoUnlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Layout ("Layout (0 mono, 1 SBS, 2 TB)", Float) = 0
        _SwapEyes ("Swap Eyes", Float) = 0
        _Projection ("Projection (0 flat, 1 dome180, 2 sphere360)", Float) = 0
        _Brightness ("Brightness", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float _Layout, _SwapEyes, _Brightness, _Projection;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 direction : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.direction = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.uv;
                if (_Projection > 0.5)
                {
                    float3 direction = normalize(i.direction);
                    // +Z is the centre; +X moves right in the image when viewed from inside.
                    float longitude = atan2(direction.x, direction.z);
                    float range = _Projection < 1.5 ? UNITY_PI : 2.0 * UNITY_PI;
                    uv = float2(longitude / range + 0.5, asin(clamp(direction.y, -1.0, 1.0)) / UNITY_PI + 0.5);
                    // atan2 jumps at the rear seam. Choose the side belonging to this triangle.
                    if (_Projection > 1.5 && abs(uv.x - i.uv.x) > 0.5)
                        uv.x += uv.x < i.uv.x ? 1.0 : -1.0;
                }
                uv = uv * _MainTex_ST.xy + _MainTex_ST.zw;
                if (_Projection > 0.5)
                {
                    // Keep bilinear sampling inside this eye at the panorama edges and rear seam.
                    float2 inset = abs(_MainTex_TexelSize.xy) * 0.5;
                    if (_Layout > 0.5 && _Layout < 1.5) inset.x *= 2.0;
                    else if (_Layout > 1.5) inset.y *= 2.0;
                    uv = clamp(uv, inset, 1.0 - inset);
                }
                float eye = abs(float(unity_StereoEyeIndex) - _SwapEyes); // 0 = left, 1 = right
                if (_Layout > 0.5 && _Layout < 1.5)
                    uv.x = uv.x * 0.5 + eye * 0.5;
                else if (_Layout > 1.5)
                    uv.y = uv.y * 0.5 + (1.0 - eye) * 0.5;
                fixed4 c = tex2D(_MainTex, uv);
                c.rgb *= _Brightness;
                return c;
            }
            ENDCG
        }
    }
}
