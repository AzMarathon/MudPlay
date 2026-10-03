using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;

namespace MudPlay.Tests;

// The Great Pyramid's rooms as the imported game data has them (12/1800-2085 plus
// the firepit 12/1239), identical on stock and Paradigm. One room per line:
// number|name|light|npc|spell, then one DIR=<exit cell> per exit, the cell verbatim.
// The script tests walk the canned climb through it, and the solver tests load it as
// their Rooms.json so the tracker follows the climb through the real layout.
internal static class PyramidRooms
{
    private const string Table = """
        1239|Scorched Cavern, Firepit|-50|0|0|S=12/1238|W=12/1240|U=12/1800 (Cast: pre-685, post-0)
        1800|Great Pyramid|-200|0|691|S=12/1801|E=12/1809|D=12/1239 (Cast: pre-732, post-0)
        1801|Great Pyramid|-200|0|691|N=12/1800|W=12/1802
        1802|Great Pyramid|-200|0|691|N=12/1803|E=12/1801
        1803|Great Pyramid|-200|0|691|N=12/1804|S=12/1802
        1804|Great Pyramid|-200|0|691|N=12/1805|S=12/1803
        1805|Great Pyramid|-200|0|691|S=12/1804|E=12/1806
        1806|Great Pyramid|-200|0|691|S=12/1807|W=12/1805
        1807|Great Pyramid|-200|0|691|N=12/1806|E=12/1808
        1808|Great Pyramid|-200|0|691|W=12/1807|U=Action [on the N exit of room 12/1811]: push block, push square block, move block
        1809|Great Pyramid|-200|0|691|S=12/1810|W=12/1800
        1810|Great Pyramid|-200|0|691|N=12/1809|E=12/1811
        1811|Great Pyramid|-200|0|691|N=12/1819 (Hidden/Needs 1 Actions, any order)|E=12/1812|W=12/1810
        1812|Great Pyramid|-200|0|691|E=12/1813|W=12/1811
        1813|Great Pyramid|-200|0|691|N=12/1814|W=12/1812
        1814|Great Pyramid|-200|0|691|S=12/1813|E=12/1815
        1815|Great Pyramid|-200|0|691|E=12/1816|W=12/1814
        1816|Great Pyramid|-200|0|691|S=12/1817|W=12/1815
        1817|Great Pyramid|-200|0|691|N=12/1816|W=12/1818
        1818|Great Pyramid|-200|0|691|E=12/1817
        1819|Great Pyramid|-200|0|691|N=12/1827|S=12/1811 (Hidden/Needs 1 Actions, any order)|E=12/1820
        1820|Great Pyramid|-200|0|691|N=12/1821|W=12/1819
        1821|Great Pyramid|-200|0|691|S=12/1820|E=12/1822
        1822|Great Pyramid|-200|0|691|N=12/1823|W=12/1821
        1823|Great Pyramid|-200|0|691|S=12/1822|E=12/1825|W=12/1824
        1824|Great Pyramid|-200|0|691|E=12/1823
        1825|Great Pyramid|-200|0|691|S=12/1826|W=12/1823
        1826|Great Pyramid|-200|0|691|N=12/1825|U=Action [on the S exit of room 12/1819]: push block, push square block, move block
        1827|Great Pyramid|-200|0|691|N=12/1828|S=12/1819
        1828|Great Pyramid|-200|0|691|N=12/1829|S=12/1827|W=12/1831
        1829|Great Pyramid|-200|0|691|N=12/1830|S=12/1828|E=12/1834
        1830|Great Pyramid|-200|0|691|S=12/1829
        1831|Great Pyramid|-200|0|691|N=12/1832|E=12/1828
        1832|Great Pyramid|-200|0|691|N=12/1833|S=12/1831
        1833|Great Pyramid|-200|0|691|S=12/1832|W=12/1836 (Hidden/Needs 1 Actions, any order)
        1834|Great Pyramid|-200|0|691|N=12/1835|W=12/1829
        1835|Great Pyramid|-200|0|691|S=12/1834|U=Action [on the W exit of room 12/1833]: push block, push square block, move block
        1836|Great Pyramid|-200|0|691|N=12/1837|E=12/1833 (Hidden/Needs 1 Actions, any order)
        1837|Great Pyramid|-200|0|691|S=12/1836|W=12/1838
        1838|Great Pyramid|-200|0|691|N=12/1842|S=12/1839|E=12/1837
        1839|Great Pyramid|-200|0|691|N=12/1838|S=12/1840
        1840|Great Pyramid|-200|0|691|N=12/1839|E=12/1841
        1841|Great Pyramid|-200|0|691|W=12/1840|U=Action [on the E exit of room 12/1836]: push block, push square block, move block
        1842|Great Pyramid|-200|0|691|N=12/1843|S=12/1838
        1843|Great Pyramid|-200|0|691|N=12/1853|S=12/1842|E=12/1844
        1844|Great Pyramid|-200|0|691|S=12/1845|W=12/1843
        1845|Great Pyramid|-200|0|691|N=12/1844|E=12/1846
        1846|Great Pyramid|-200|0|691|S=12/1847|W=12/1845
        1847|Great Pyramid|-200|0|691|N=12/1846|E=12/1848
        1848|Great Pyramid|-200|0|691|N=12/1849|W=12/1847
        1849|Great Pyramid|-200|0|691|N=12/1850|S=12/1848
        1850|Great Pyramid|-200|0|691|S=12/1849|W=12/1851
        1851|Great Pyramid|-200|0|691|N=12/1852 (Hidden/Needs 1 Actions, any order)|E=12/1850
        1852|Great Pyramid|-200|0|691|S=12/1851 (Hidden/Needs 1 Actions, any order)|E=12/1858|W=12/1857
        1853|Great Pyramid|-200|0|691|N=12/1854|S=12/1843
        1854|Great Pyramid|-200|0|691|S=12/1853|E=12/1855
        1855|Great Pyramid|-200|0|691|E=12/1856|W=12/1854
        1856|Great Pyramid|-200|0|691|W=12/1855|U=Action [on the N exit of room 12/1851]: push block, push square block, move block
        1857|Great Pyramid|-200|0|691|E=12/1852
        1858|Great Pyramid|-200|0|691|N=12/1859|W=12/1852
        1859|Great Pyramid|-200|0|691|S=12/1858|E=12/1860
        1860|Great Pyramid|-200|0|691|S=12/1861|E=12/1862|W=12/1859
        1861|Great Pyramid|-200|0|691|N=12/1860|U=Action [on the S exit of room 12/1852]: push block, push square block, move block
        1862|Great Pyramid|-200|0|691|E=12/1863|W=12/1860
        1863|Great Pyramid|-200|0|691|E=12/1864|W=12/1862
        1864|Great Pyramid|-200|0|691|E=12/1865|W=12/1863
        1865|Great Pyramid|-200|0|691|S=12/1866|W=12/1864
        1866|Great Pyramid|-200|0|691|N=12/1865|E=12/1867
        1867|Great Pyramid|-200|0|691|N=12/1868|W=12/1866
        1868|Great Pyramid|-200|0|691|S=12/1867|E=12/1869
        1869|Great Pyramid|-200|0|691|S=12/1870|W=12/1868
        1870|Great Pyramid|-200|0|691|N=12/1869|S=12/1871
        1871|Great Pyramid|-200|0|691|N=12/1870|W=12/1872
        1872|Great Pyramid|-200|0|691|S=12/1873 (Hidden/Needs 1 Actions, any order)|E=12/1871|W=12/1874
        1873|Great Pyramid|-200|0|691|N=12/1872 (Hidden/Needs 1 Actions, any order)|S=12/1889
        1874|Great Pyramid|-200|0|691|E=12/1872|W=12/1875
        1875|Great Pyramid|-200|0|691|N=12/1877|E=12/1874|W=12/1876
        1876|Great Pyramid|-200|0|691|E=12/1875
        1877|Great Pyramid|-200|0|691|S=12/1875|W=12/1878
        1878|Great Pyramid|-200|0|691|E=12/1877|W=12/1879
        1879|Great Pyramid|-200|0|691|S=12/1880|E=12/1878
        1880|Great Pyramid|-200|0|691|N=12/1879|W=12/1881
        1881|Great Pyramid|-200|0|691|S=12/1882|E=12/1880
        1882|Great Pyramid|-200|0|691|N=12/1881|S=12/1883
        1883|Great Pyramid|-200|0|691|N=12/1882|E=12/1884
        1884|Great Pyramid|-200|0|691|N=12/1885|W=12/1883
        1885|Great Pyramid|-200|0|691|S=12/1884|E=12/1886
        1886|Great Pyramid|-200|0|691|E=12/1887|W=12/1885
        1887|Great Pyramid|-200|0|691|E=12/1888|W=12/1886
        1888|Great Pyramid|-200|0|691|W=12/1887|U=Action [on the S exit of room 12/1872]: push block, push square block, move block
        1889|Great Pyramid|-200|0|691|N=12/1873|E=12/1890
        1890|Great Pyramid|-200|0|691|N=12/1891|S=12/1892|W=12/1889
        1891|Great Pyramid|-200|0|691|S=12/1890|U=Action [on the N exit of room 12/1873]: push block, push square block, move block
        1892|Great Pyramid|-200|0|691|N=12/1890|S=12/1893
        1893|Great Pyramid|-200|0|691|N=12/1892|S=12/1894
        1894|Great Pyramid|-200|0|691|N=12/1893|S=12/1895
        1895|Great Pyramid|-200|0|691|N=12/1894|S=12/1896
        1896|Great Pyramid|-200|0|691|N=12/1895|S=12/1897
        1897|Great Pyramid|-200|0|691|N=12/1896|W=12/1898
        1898|Great Pyramid|-200|0|691|E=12/1897|W=12/1899
        1899|Great Pyramid|-200|0|691|N=12/1900|E=12/1898
        1900|Great Pyramid|-200|0|691|S=12/1899|E=12/1901
        1901|Great Pyramid|-200|0|691|N=12/1902|W=12/1900
        1902|Great Pyramid|-200|0|691|N=12/1903|S=12/1901
        1903|Great Pyramid|-200|0|691|N=12/1904|S=12/1902|W=12/1906
        1904|Great Pyramid|-200|0|691|N=12/1905|S=12/1903|W=12/1909 (Hidden/Needs 1 Actions, any order)
        1905|Great Pyramid|-200|0|691|S=12/1904
        1906|Great Pyramid|-200|0|691|S=12/1907|E=12/1903
        1907|Great Pyramid|-200|0|691|N=12/1906|W=12/1908
        1908|Great Pyramid|-200|0|691|E=12/1907|U=Action [on the W exit of room 12/1904]: push block, push square block, move block
        1909|Great Pyramid|-200|0|691|N=12/1910|E=12/1904 (Hidden/Needs 1 Actions, any order)
        1910|Great Pyramid|-200|0|691|N=12/1911|S=12/1909
        1911|Great Pyramid|-200|0|691|S=12/1910|W=12/1912
        1912|Great Pyramid|-200|0|691|E=12/1911|W=12/1913
        1913|Great Pyramid|-200|0|691|S=12/1914|E=12/1912
        1914|Great Pyramid|-200|0|691|N=12/1913|S=12/1918|E=12/1915
        1915|Great Pyramid|-200|0|691|S=12/1916|W=12/1914
        1916|Great Pyramid|-200|0|691|N=12/1915|S=12/1917
        1917|Great Pyramid|-200|0|691|N=12/1916|U=Action [on the E exit of room 12/1909]: push block, push square block, move block
        1918|Great Pyramid|-200|0|691|N=12/1914|W=12/1919
        1919|Great Pyramid|-200|0|691|N=12/1920|E=12/1918
        1920|Great Pyramid|-200|548|691|S=12/1919|U=12/1921 (Hidden/Needs 1 Actions, any order)
        1921|Great Pyramid|-999|0|692|S=12/1922
        1922|Great Pyramid|-999|0|692|N=12/1921|E=12/1923
        1923|Great Pyramid|-999|0|692|S=12/1924|W=12/1922
        1924|Great Pyramid|-999|0|692|N=12/1923|E=12/1925
        1925|Great Pyramid|-999|0|692|S=12/1926|E=12/1959|W=12/1924
        1926|Great Pyramid|-999|0|692|N=12/1925|E=12/1927
        1927|Great Pyramid|-999|0|692|E=12/1928|W=12/1926
        1928|Great Pyramid|-999|0|692|S=12/1929|W=12/1927
        1929|Great Pyramid|-999|0|692|N=12/1928|W=12/1930
        1930|Great Pyramid|-999|0|692|E=12/1929|W=12/1931
        1931|Great Pyramid|-999|0|692|E=12/1930|W=12/1932
        1932|Great Pyramid|-999|0|692|N=12/1933|E=12/1931
        1933|Great Pyramid|-999|0|692|S=12/1932|W=12/1934
        1934|Great Pyramid|-999|0|692|N=12/1949|E=12/1933|W=12/1935
        1935|Great Pyramid|-999|0|692|S=12/1936|E=12/1934
        1936|Great Pyramid|-999|0|692|N=12/1935|E=12/1937|W=12/1938
        1937|Great Pyramid|-999|0|692|W=12/1936
        1938|Great Pyramid|-999|0|692|E=12/1936|W=12/1939
        1939|Great Pyramid|-999|0|692|E=12/1938|W=12/1940
        1940|Great Pyramid|-999|0|692|N=12/1941|E=12/1939
        1941|Great Pyramid|-999|0|692|N=12/1942|S=12/1940
        1942|Great Pyramid|-999|0|692|N=12/1943|S=12/1941|E=12/1945
        1943|Great Pyramid|-999|0|692|N=12/1944|S=12/1942
        1944|Great Pyramid|-999|0|692|S=12/1943
        1945|Great Pyramid|-999|0|692|S=12/1946|W=12/1942
        1946|Great Pyramid|-999|0|692|N=12/1945|E=12/1947
        1947|Great Pyramid|-999|0|692|N=12/1948|W=12/1946
        1948|Great Pyramid|-999|0|692|S=12/1947
        1949|Great Pyramid|-999|0|692|S=12/1934|W=12/1950
        1950|Great Pyramid|-999|0|692|N=12/1951|E=12/1949
        1951|Great Pyramid|-999|0|692|N=12/1952|S=12/1950
        1952|Great Pyramid|-999|0|692|N=12/1953|S=12/1951
        1953|Great Pyramid|-999|0|692|N=12/1954|S=12/1952
        1954|Great Pyramid|-999|0|692|S=12/1953|W=12/1955
        1955|Great Pyramid|-999|0|692|E=12/1954|W=12/1956
        1956|Great Pyramid|-999|0|692|N=12/1957|E=12/1955
        1957|Great Pyramid|-999|0|692|S=12/1956|E=12/1958
        1958|Great Pyramid|-999|0|692|W=12/1957
        1959|Great Pyramid|-999|0|692|N=12/1960|W=12/1925
        1960|Great Pyramid|-999|0|692|S=12/1959|W=12/1961
        1961|Great Pyramid|-999|0|692|N=12/1962|E=12/1960
        1962|Great Pyramid|-999|0|692|N=12/1963|S=12/1961
        1963|Great Pyramid|-999|0|692|N=12/1964|S=12/1962
        1964|Great Pyramid|-999|0|692|S=12/1963|E=12/1965
        1965|Great Pyramid|-999|0|692|N=12/1966|W=12/1964
        1966|Great Pyramid|-999|0|692|S=12/1965|E=12/1967
        1967|Great Pyramid|-999|0|692|N=12/1975|S=12/1968|W=12/1966
        1968|Great Pyramid|-999|0|692|N=12/1967|S=12/1969
        1969|Great Pyramid|-999|0|692|N=12/1968|W=12/1970
        1970|Great Pyramid|-999|0|692|S=12/1971|E=12/1969
        1971|Great Pyramid|-999|0|692|N=12/1970|E=12/1972
        1972|Great Pyramid|-999|0|692|S=12/1973|W=12/1971
        1973|Great Pyramid|-999|0|692|N=12/1972|S=12/1974
        1974|Great Pyramid|-999|0|692|N=12/1973
        1975|Great Pyramid|-999|0|692|S=12/1967|W=12/1976
        1976|Great Pyramid|-999|0|692|E=12/1975|W=12/1977
        1977|Great Pyramid|-999|0|692|E=12/1976|W=12/1978
        1978|Great Pyramid|-999|0|692|E=12/1977|W=12/1979
        1979|Great Pyramid|-999|0|692|S=12/1980|E=12/1978
        1980|Great Pyramid|-999|0|692|N=12/1979|S=12/1981|W=12/1988
        1981|Great Pyramid|-999|0|692|N=12/1980|S=12/1982
        1982|Great Pyramid|-999|0|692|N=12/1981|E=12/1983
        1983|Great Pyramid|-999|0|692|N=12/1985|S=12/1984|W=12/1982
        1984|Great Pyramid|-999|0|692|N=12/1983
        1985|Great Pyramid|-999|0|692|N=12/1986|S=12/1983
        1986|Great Pyramid|-999|0|692|S=12/1985|E=12/1987
        1987|Great Pyramid|-999|0|692|W=12/1986
        1988|Great Pyramid|-999|0|692|N=12/1989|E=12/1980
        1989|Great Pyramid|-999|0|692|S=12/1988|W=12/1990
        1990|Great Pyramid|-999|0|692|E=12/1989|W=12/1991
        1991|Great Pyramid|-999|0|692|E=12/1990|W=12/1992
        1992|Great Pyramid|-999|0|692|S=12/1993|E=12/1991
        1993|Great Pyramid|-999|0|692|N=12/1992|S=12/1994
        1994|Great Pyramid|-999|0|692|N=12/1993|S=12/1995
        1995|Great Pyramid|-999|0|692|N=12/1994|E=12/1996
        1996|Great Pyramid|-999|0|692|E=12/1997|W=12/1995
        1997|Great Pyramid|-999|0|692|S=12/1998|W=12/1996
        1998|Great Pyramid|-999|0|692|N=12/1997|S=12/1999
        1999|Great Pyramid|-999|0|692|N=12/1998|W=12/2000
        2000|Great Pyramid|-999|0|692|N=12/2001|E=12/1999
        2001|Great Pyramid|-50|549|692|S=12/2000|U=12/2002 (Hidden/Needs 1 Actions, any order)
        2002|Great Pyramid|-200|0|700|N=12/2012 (Door [1000 picklocks/strength])|S=12/2046 (Door [1000 picklocks/strength])|E=12/2003 (Door)|W=12/2252 (Door [1000 picklocks/strength])
        2003|Great Pyramid|-200|0|700|N=12/2013 (Door [1000 picklocks/strength])|S=12/2045 (Door [1000 picklocks/strength])|E=12/2004 (Door)|W=12/2002 (Door)
        2004|Great Pyramid|-200|0|700|N=12/2006 (Door [1000 picklocks/strength])|S=12/2049 (Door [1000 picklocks/strength])|E=12/2005 (Door [1000 picklocks/strength])|W=12/2003 (Door)
        2005|Great Pyramid|-200|598|700|N=12/2007 (Door [1000 picklocks/strength])|S=12/2048 (Door [1000 picklocks/strength])|E=12/2032 (Door)|W=12/2004 (Door [1000 picklocks/strength])
        2006|Great Pyramid|-200|0|700|N=12/2009 (Door [1000 picklocks/strength])|S=12/2004 (Door [1000 picklocks/strength])|E=12/2007 (Door [1000 picklocks/strength])|W=12/2013 (Door)
        2007|Great Pyramid|-200|0|700|N=12/2008 (Door)|S=12/2005 (Door [1000 picklocks/strength])|E=12/2033 (Door [1000 picklocks/strength])|W=12/2006 (Door [1000 picklocks/strength])
        2008|Great Pyramid|-200|0|700|N=12/2017 (Door [1000 picklocks/strength])|S=12/2007 (Door)|E=12/2019 (Door [1000 picklocks/strength])|W=12/2009 (Door)
        2009|Great Pyramid|-200|0|700|N=12/2016 (Door [1000 picklocks/strength])|S=12/2006 (Door [1000 picklocks/strength])|E=12/2008 (Door)|W=12/2010 (Door)
        2010|Great Pyramid|-200|0|700|N=12/2015 (Door [1000 picklocks/strength])|S=12/2013 (Door [1000 picklocks/strength])|E=12/2009 (Door)|W=12/2011 (Door [1000 picklocks/strength])
        2011|Great Pyramid|-200|0|700|N=12/2014 (Door)|S=12/2012 (Door)|E=12/2010 (Door [1000 picklocks/strength])|W=12/2252 (Door [1000 picklocks/strength])
        2012|Great Pyramid|-200|0|700|N=12/2011 (Door)|S=12/2002 (Door [1000 picklocks/strength])|E=12/2013 (Door)|W=12/2252 (Door [1000 picklocks/strength])
        2013|Great Pyramid|-200|0|700|N=12/2010 (Door [1000 picklocks/strength])|S=12/2003 (Door [1000 picklocks/strength])|E=12/2006 (Door)|W=12/2012 (Door)
        2014|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2011 (Door)|E=12/2015 (Door)|W=12/2252 (Door [1000 picklocks/strength])
        2015|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2010 (Door [1000 picklocks/strength])|E=12/2016 (Door)|W=12/2014 (Door)
        2016|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2009 (Door [1000 picklocks/strength])|E=12/2017 (Door)|W=12/2015 (Door)
        2017|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2008 (Door [1000 picklocks/strength])|E=12/2018 (Door [1000 picklocks/strength])|W=12/2016 (Door)
        2018|Great Pyramid|-200|0|700|N=12/2252 (Key: 1980 [or 1000 picklocks/strength])|S=12/2019 (Door)|E=12/2023 (Door)|W=12/2017 (Door [1000 picklocks/strength])
        2019|Great Pyramid|-200|0|700|N=12/2018 (Door)|S=12/2033 (Door [1000 picklocks/strength])|E=12/2020 (Door [1000 picklocks/strength])|W=12/2008 (Door [1000 picklocks/strength])
        2020|Great Pyramid|-200|0|700|N=12/2023 (Door [1000 picklocks/strength])|S=12/2024 (Door)|E=12/2021 (Door)|W=12/2019 (Door [1000 picklocks/strength])
        2021|Great Pyramid|-200|0|700|N=12/2022 (Door [1000 picklocks/strength])|S=12/2025 (Door [1000 picklocks/strength])|E=12/2252 (Door [1000 picklocks/strength])|W=12/2020 (Door)
        2022|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2021 (Door [1000 picklocks/strength])|E=12/2252 (Door [1000 picklocks/strength])|W=12/2023 (Door)
        2023|Great Pyramid|-200|0|700|N=12/2252 (Door [1000 picklocks/strength])|S=12/2020 (Door [1000 picklocks/strength])|E=12/2022 (Door)|W=12/2018 (Door)
        2024|Great Pyramid|-200|0|700|N=12/2020 (Door)|S=12/2031 (Door)|E=12/2025 (Door)|W=12/2033 (Door [1000 picklocks/strength])
        2025|Great Pyramid|-200|0|700|N=12/2021 (Door [1000 picklocks/strength])|S=12/2026 (Door [1000 picklocks/strength])|E=12/2252 (Door [1000 picklocks/strength])|W=12/2024 (Door)
        2026|Great Pyramid|-200|0|700|N=12/2025 (Door [1000 picklocks/strength])|S=12/2027 (Door)|E=12/2252 (Door [1000 picklocks/strength])|W=12/2031 (Door [1000 picklocks/strength])
        2027|Great Pyramid|-200|0|700|N=12/2026 (Door)|S=12/2028 (Door)|E=12/2252 (Door [1000 picklocks/strength])|W=12/2030 (Door [1000 picklocks/strength])
        2028|Great Pyramid|-200|0|700|N=12/2027 (Door)|S=12/2036 (Door)|E=12/2252 (Door [1000 picklocks/strength])|W=12/2029 (Door [1000 picklocks/strength])
        2029|Great Pyramid|-200|0|700|N=12/2030 (Door)|S=12/2037 (Door [1000 picklocks/strength])|E=12/2028 (Door [1000 picklocks/strength])|W=12/2035 (Door [1000 picklocks/strength])
        2030|Great Pyramid|-200|0|700|N=12/2031 (Door [1000 picklocks/strength])|S=12/2029 (Door)|E=12/2027 (Door [1000 picklocks/strength])|W=12/2034 (Door [1000 picklocks/strength])
        2031|Great Pyramid|-200|0|700|N=12/2024 (Door)|S=12/2030 (Door [1000 picklocks/strength])|E=12/2026 (Door [1000 picklocks/strength])|W=12/2032 (Door)
        2032|Great Pyramid|-200|0|700|N=12/2033 (Door)|S=12/2034 (Door [1000 picklocks/strength])|E=12/2031 (Door)|W=12/2005 (Door)
        2033|Great Pyramid|-200|0|700|N=12/2019 (Door [1000 picklocks/strength])|S=12/2032 (Door)|E=12/2024 (Door [1000 picklocks/strength])|W=12/2007 (Door [1000 picklocks/strength])
        2034|Great Pyramid|-200|0|700|N=12/2032 (Door [1000 picklocks/strength])|S=12/2035 (Door)|E=12/2030 (Door [1000 picklocks/strength])|W=12/2048 (Key: 1175 [or 1000 picklocks/strength])
        2035|Great Pyramid|-200|0|700|N=12/2034 (Door)|S=12/2038 (Door [1000 picklocks/strength])|E=12/2029 (Door [1000 picklocks/strength])|W=12/2051 (Door [1000 picklocks/strength])
        2036|Great Pyramid|-200|0|700|N=12/2028 (Door)|S=12/2252 (Door [1000 picklocks/strength])|E=12/2252 (Door [1000 picklocks/strength])|W=12/2037 (Door)
        2037|Great Pyramid|-200|0|700|N=12/2029 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2036 (Door)|W=12/2038 (Door)
        2038|Great Pyramid|-200|0|700|N=12/2035 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2037 (Door)|W=12/2039 (Door [1000 picklocks/strength])
        2039|Great Pyramid|-200|0|700|N=12/2051 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2038 (Door [1000 picklocks/strength])|W=12/2040 (Door)
        2040|Great Pyramid|-200|0|700|N=12/2050 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2039 (Door)|W=12/2041 (Door)
        2041|Great Pyramid|-200|0|700|N=12/2044 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2040 (Door)|W=12/2042 (Door)
        2042|Great Pyramid|-200|0|700|N=12/2047 (Door [1000 picklocks/strength])|S=12/2252 (Door [1000 picklocks/strength])|E=12/2041 (Door)|W=12/2252 (Door [1000 picklocks/strength])
        2043|Great Pyramid|-200|0|700|S=12/2041
        2044|Great Pyramid|-200|0|700|N=12/2045 (Door)|S=12/2041 (Door [1000 picklocks/strength])|E=12/2050 (Door [1000 picklocks/strength])|W=12/2047 (Door [1000 picklocks/strength])
        2045|Great Pyramid|-200|0|700|N=12/2003 (Door [1000 picklocks/strength])|S=12/2044 (Door)|E=12/2049 (Door [1000 picklocks/strength])|W=12/2046 (Door)
        2046|Great Pyramid|-200|0|700|N=12/2002 (Door [1000 picklocks/strength])|S=12/2047 (Door)|E=12/2045 (Door)|W=12/2252 (Door [1000 picklocks/strength])
        2047|Great Pyramid|-200|0|700|N=12/2046 (Door)|S=12/2042 (Door [1000 picklocks/strength])|E=12/2044 (Door [1000 picklocks/strength])|W=12/2252 (Door [1000 picklocks/strength])
        2048|Great Pyramid|-200|0|700|N=12/2005 (Door [1000 picklocks/strength])|S=12/2051 (Door [1000 picklocks/strength])|E=12/2034 (Key: 1175 [or 1000 picklocks/strength])|W=12/2049 (Door)
        2049|Great Pyramid|-200|0|700|N=12/2004 (Door [1000 picklocks/strength])|S=12/2050 (Door)|E=12/2048 (Door)|W=12/2045 (Door [1000 picklocks/strength])
        2050|Great Pyramid|-200|0|700|N=12/2049 (Door)|S=12/2040 (Door [1000 picklocks/strength])|E=12/2051 (Door)|W=12/2044 (Door [1000 picklocks/strength])
        2051|Great Pyramid|-200|550|700|N=12/2048 (Door [1000 picklocks/strength])|S=12/2039 (Door [1000 picklocks/strength])|E=12/2035 (Door [1000 picklocks/strength])|W=12/2050 (Door)|U=12/2052 (Hidden/Needs 1 Actions, any order)
        2052|Great Pyramid|-200|551|691|N=12/2072 (Cast: pre-0, post-702)|E=12/2053|W=12/2054
        2053|Great Pyramid|-200|0|691|N=12/2075 (Cast: pre-0, post-702)|E=12/2055|W=12/2052
        2054|Great Pyramid|-200|0|691|N=12/2071 (Cast: pre-0, post-702)|E=12/2052|W=12/2067
        2055|Great Pyramid|-200|0|691|N=12/2056|W=12/2053
        2056|Great Pyramid|-200|0|691|N=12/2057|S=12/2055|W=12/2075 (Cast: pre-0, post-702)
        2057|Great Pyramid|-200|0|691|N=12/2058|S=12/2056|W=12/2074 (Cast: pre-0, post-702)
        2058|Great Pyramid|-200|0|691|N=12/2059|S=12/2057|W=12/2073 (Cast: pre-0, post-735)
        2059|Great Pyramid|-200|0|691|S=12/2058|W=12/2060
        2060|Great Pyramid|-200|0|691|S=12/2073 (Cast: pre-0, post-702)|E=12/2059|W=12/2061
        2061|Great Pyramid|-200|0|691|S=12/2068 (Cast: pre-0, post-701)|E=12/2060|W=12/2062
        2062|Great Pyramid|-200|0|691|S=12/2069 (Cast: pre-0, post-702)|E=12/2061|W=12/2063
        2063|Great Pyramid|-200|0|691|S=12/2064|E=12/2062
        2064|Great Pyramid|-200|0|691|N=12/2063|S=12/2065|E=12/2069 (Cast: pre-0, post-702)
        2065|Great Pyramid|-200|0|691|N=12/2064|S=12/2066|E=12/2070 (Cast: pre-0, post-702)
        2066|Great Pyramid|-200|0|691|N=12/2065|S=12/2067|E=12/2071 (Cast: pre-0, post-702)
        2067|Great Pyramid|-200|0|691|N=12/2066|E=12/2054
        2068|Great Pyramid|-200|0|691|N=12/2061 (Cast: pre-0, post-702)|S=12/2076 (Cast: pre-0, post-702)|E=12/2073 (Cast: pre-0, post-702)|W=12/2069 (Cast: pre-0, post-701)
        2069|Great Pyramid|-200|0|691|N=12/2062 (Cast: pre-0, post-702)|S=12/2070 (Cast: pre-0, post-701)|E=12/2068 (Cast: pre-0, post-702)|W=12/2064 (Cast: pre-0, post-702)
        2070|Great Pyramid|-200|0|691|N=12/2069 (Cast: pre-0, post-702)|S=12/2071 (Cast: pre-0, post-701)|E=12/2076 (Cast: pre-0, post-702)|W=12/2065 (Cast: pre-0, post-702)
        2071|Great Pyramid|-200|0|691|N=12/2070 (Cast: pre-0, post-702)|S=12/2054 (Cast: pre-0, post-702)|E=12/2072 (Cast: pre-0, post-701)|W=12/2066 (Cast: pre-0, post-702)
        2072|Great Pyramid|-200|0|691|N=12/2076 (Cast: pre-0, post-702)|S=12/2052 (Cast: pre-0, post-733)|E=12/2075 (Cast: pre-0, post-702)|W=12/2071 (Cast: pre-0, post-702)
        2073|Great Pyramid|-200|0|691|N=12/2060 (Cast: pre-0, post-702)|S=12/2074 (Cast: pre-0, post-701)|E=12/2058 (Cast: pre-0, post-702)|W=12/2068 (Cast: pre-0, post-702)
        2074|Great Pyramid|-200|0|691|N=12/2073 (Cast: pre-0, post-702)|S=12/2075 (Cast: pre-0, post-702)|E=12/2057 (Cast: pre-0, post-702)|W=12/2076 (Cast: pre-0, post-702)|U=12/2077
        2075|Great Pyramid|-200|0|691|N=12/2074 (Cast: pre-0, post-702)|S=12/2053 (Cast: pre-0, post-702)|E=12/2056 (Cast: pre-0, post-702)|W=12/2072 (Cast: pre-0, post-702)
        2076|Great Pyramid|-200|0|691|N=12/2068 (Cast: pre-0, post-702)|S=12/2072 (Cast: pre-0, post-702)|E=12/2074 (Cast: pre-0, post-702)|W=12/2070 (Cast: pre-0, post-702)
        2077|Great Pyramid|-200|0|691|N=12/2084|S=12/2078
        2078|Great Pyramid|-200|0|691|N=12/2077|W=12/2079
        2079|Great Pyramid|-200|0|691|E=12/2078|W=12/2080
        2080|Great Pyramid|-200|0|691|N=12/2081|E=12/2079
        2081|Great Pyramid|-200|0|691|N=12/2082|S=12/2080|E=12/2085
        2082|Great Pyramid|-200|0|691|S=12/2081|E=12/2083
        2083|Great Pyramid|-200|0|691|E=12/2084|W=12/2082
        2084|Great Pyramid|-200|0|691|S=12/2077|W=12/2083
        2085|Great Pyramid|-200|552|691|W=12/2081|U=12/2250 (Hidden/Needs 1 Actions, any order)
        """;

    private static readonly string[] DirColumns = { "N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D" };

    private static IEnumerable<string[]> Rows()
        => Table.Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                .Select(l => l.Split('|'));

    // Raw exit cells per room, keyed by direction column.
    public static IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> ExitCells { get; } = Rows()
        .ToDictionary(
            r => int.Parse(r[0]),
            r => (IReadOnlyDictionary<string, string>)r.Skip(5)
                .Select(c => c.Split('=', 2))
                .ToDictionary(c => c[0], c => c[1]));

    // The room a plain walk in that direction lands in, or null when the room has no
    // exit there. Qualifiers (doors, gates, casts) are ignored — the cell's target is
    // where the exit leads once it is open.
    public static int? Target(int room, Direction dir)
    {
        if (!ExitCells.TryGetValue(room, out var cells)) return null;
        if (!cells.TryGetValue(dir.ToString(), out string? cell)) return null;
        int slash = cell.IndexOf('/');
        if (slash <= 0 || !int.TryParse(cell[..slash], out int map) || map != PyramidScript.PyramidMap) return null;
        int end = slash + 1;
        while (end < cell.Length && char.IsDigit(cell[end])) end++;
        return int.Parse(cell[(slash + 1)..end]);
    }

    // The table as a Rooms.json the game-data cache can load.
    public static string Json()
    {
        StringBuilder sb = new("[");
        bool first = true;
        foreach (string[] r in Rows())
        {
            if (!first) sb.Append(',');
            first = false;
            var cells = r.Skip(5).Select(c => c.Split('=', 2)).ToDictionary(c => c[0], c => c[1]);
            sb.Append($"{{\"Map Number\":12,\"Room Number\":{r[0]},\"Name\":\"{r[1]}\",\"Light\":{r[2]},")
              .Append($"\"Shop\":0,\"NPC\":{r[3]},\"CMD\":0,\"Spell\":{r[4]},\"Lair\":\"\",\"Delay\":5");
            foreach (string col in DirColumns)
                sb.Append($",\"{col}\":\"{(cells.TryGetValue(col, out string? v) ? v : "0")}\"");
            sb.Append('}');
        }
        return sb.Append(']').ToString();
    }
}
