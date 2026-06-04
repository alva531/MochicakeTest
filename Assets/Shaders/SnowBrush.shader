Shader "Custom/SnowBrush"
{
    Properties { _MainTex("", 2D) = "" {} }
    SubShader { Pass {
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float2 _BrushUV;
        float  _BrushWidth;
        float  _BrushLength;
        float  _Strength;

        fixed4 frag(v2f_img i) : SV_Target
        {
            float existing = tex2D(_MainTex, i.uv).r;
            float2 delta   = i.uv - _BrushUV;
            float2 norm    = float2(delta.x / _BrushWidth, delta.y / _BrushLength);
            float  dist    = length(norm);
            float  brush   = smoothstep(1.0, 0.0, dist) * _Strength;

            brush *= step(0.0, delta.y);

            return fixed4(max(existing, brush), 0, 0, 1);
        }
        ENDCG
    }}
}