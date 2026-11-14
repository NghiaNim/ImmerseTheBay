// Unlit shader that shows a different half of the texture to each eye.
// _Layout: 0 = mono, 1 = side-by-side (left eye = left half), 2 = top-bottom (left eye = top half).
Shader "ImmerseTheBay/StereoUnlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Layout ("Layout (0 mono, 1 SBS, 2 TB)", Float) = 0
        _SwapEyes ("Swap Eyes", Float) = 0
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
            float _Layout, _SwapEyes, _Brightness;

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = TRANSFORM_TEX(v.uv, _MainTex);
                float eye = abs(float(unity_StereoEyeIndex) - _SwapEyes); // 0 = left, 1 = right
                if (_Layout > 0.5 && _Layout < 1.5)
                    uv.x = uv.x * 0.5 + eye * 0.5;
                else if (_Layout > 1.5)
                    uv.y = uv.y * 0.5 + (1.0 - eye) * 0.5;
                o.uv = uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                c.rgb *= _Brightness;
                return c;
            }
            ENDCG
        }
    }
}
