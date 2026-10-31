var option = {
	0: {
		tooltip: {
			trigger: 'item',
			formatter: "{a} <br/>{b} : {c} "
		},
		legend: {
			orient: 'vertical',
			x: '210',
			y: '145',
			data: [{
				name: '主胜',
				icon: 'stack',
				textStyle: {
					color: '#e40000'
				}
			}, { 
				name: '平局',
				icon: 'stack',
				textStyle: {
					color: '#34be34'
				}
			}, {
				name: '客胜',
				icon: 'stack',
				textStyle: {
					color: '#2e2eff'
				}
			}]
		},
		color: ['#e40000', '#34be34', '#2e2eff'],
		calculable: false,
		series: [{
			name: '成交量',
			type: 'pie',
			radius: ['44%', '56%'],

			itemStyle: {
				normal: {

					label: {
						show: false,
						formatter: "{d}%",
						position: ["50%", "50%"],
						textStyle: {
							fontSize: 14,
							color: '#333'
						}

					},
					labelLine: {
						show: false,
						length: -20

					}
				}
			},
			center: ['100', '170'],
			data: [{
				value: trade_list.split(",")[0],
				name: '主胜'
			}, {
				value: trade_list.split(",")[1],
				name: '平局'
			}, {
				value: trade_list.split(",")[2],
				name: '客胜'
			} ]
		}]
	},

	1: {
		tooltip: {
			trigger: 'items',
			formatter: "{a} <br/>{b} : {c} "
		},
		grid: {
			borderWidth: 0,
			width: 340
		},
		color: ['#e40000', '#34be34', '#2e2eff'],
		legend: {
			orient: 'horizontal',
			x: '270',
			y: '20',

			data: [{
				name: '主胜',
				icon: 'stack',
				textStyle: {
					color: '#e40000'
				}
			}, {
				name: '平局',
				icon: 'stack',
				textStyle: {
					color: '#34be34'
				}
			}, {
				name: '客胜',
				icon: 'stack',
				textStyle: {
					color: '#2e2eff'
				}
			}]
		},
		xAxis: [{
			type: 'category',
			name: '日期',
			boundaryGap: true,
			data:trade_time.split(","),
			boundaryGap: 'true',
			splitLine: 'true',
			axisTick: 'false',
			nameTextStyle: {
				color: '#99999'
			},
			axisLabel: {
				//X轴刻度配置
				interval: parseInt(time_len)-2 //0：表示全部显示不间隔；auto:表示自动根据刻度个数和宽度自动设置间隔个数
			}
		}],
		yAxis: [{
			name: '单位：元',
			type: 'value',
			axisLabel: {
				formatter : '{value}%' 
			},
			splitNumber: 6,
			show: 'false',
			axisLabel: {
                formatter: y_show
            }
		}],
		series: [

			{
				name: '主胜',
				type: 'line',
				smooth: 'true',
				symbol: 'none',
				stack: null,
				itemStyle: {
					normal: {
						lineStyle: {
							color: '#e40000'
						}
					}
				},
				data: trade_win.split(",")
			},

			{
				name: '平局',
				type: 'line',
				smooth: 'true',
				symbol: 'none',
				stack: null,
				itemStyle: {
					normal: {
						lineStyle: {
							color: '#34be34'
						}
					}
				},
				data: trade_draw.split(",")
			}, {
				name: '客胜',
				type: 'line',
				smooth: 'true',
				symbol: 'none',
				stack: null,
				itemStyle: {
					normal: {
						lineStyle: {
							color: '#2e2eff'
						}
					}
				},
				data: trade_lost.split(",")
			}]
	},
	/*大额交易分析*/
	2: {
		tooltip: {
			trigger: 'item',
			formatter: "{a} <br/>{b} : {c} "
		},
		legend: {
			orient: 'vertical',
			x: '210',
			y: '145',
			data: [{
				name: '主胜',
				icon: 'stack',
				textStyle: {
					color: '#e40000'
				}
			}, {
				name: '平局',
				icon: 'stack',
				textStyle: {
					color: '#34be34'
				}
			}, {
				name: '客胜',
				icon: 'stack',
				textStyle: {
					color: '#2e2eff'
				}
			}]
		},
		color: ['#e40000', '#34be34', '#2e2eff'],
		calculable: false,
		series: [{
			name: '成交量',
			type: 'pie',
			radius: ['44%', '56%'],

			itemStyle: {
				normal: {

					label: {
						show: false,
						formatter: "{d}%",
						position: ["50%", "50%"],
						textStyle: {
							fontSize: 14,
							color: '#333'
						}

					},
					labelLine: {
						show: false,
						length: -20

					}
				}
			},
			center: ['100', '170'],
			data: [{
				value: big_list.split(",")[0],
				name: '主胜'
			}, {
				value: big_list.split(",")[1],
				name: '平局'
			}, {
				value: big_list.split(",")[2],
				name: '客胜'
			}]
		}]
	},
	3: {
        
		tooltip: {
			trigger: 'axis',
			axisPointer: {
				type: 'shadow'
			}
		},
		legend: {
			x: '275',
			orient: 'horizontal',
			y: '20',
			itemWidth: 18,
			data: ['买入', '卖出']

		},
		color: ['#61a0a8', '#d48265'],
		grid: {
			show: 'false'
		},
		xAxis: {
			type: 'category',
			data: ['主胜', '平局', '客胜'],
			nameGap: '5',
			boundaryGap: 'true',
			splitLine: 'false',
			axisTick: 'false'
		},
		yAxis: {
			type: 'value',
			boundaryGap: [0, 0.01],
			axisLabel: {
                formatter: y_show
            }
		},
		series: [{
			name: '买入',
			type: 'bar',
			itemStyle: {
                    normal: {　　　　　　　　　　　　　
                        color: function(params) {
                            var colorList = [
                               '#e40000', '#34be34', '#2e2eff'
                            ];
                            return colorList[params.dataIndex]
                        }
                    }
                },
			data: big_buy.split(",")
		}, {
			name: '卖出',
			type: 'bar',
            itemStyle: {
                    normal: {　　　　　　　　　　　　　
                        color: function(params) {
                            var colorList = [
                               '#F17F7F','#99DE99','#9696FF'
                            ];
                            return colorList[params.dataIndex]
                        }
                    }
                },
			data:  big_sell.split(",")
		}]
	}
};
var chartItms = $(".chartList2"),myChart=[];
for (var i = 0, l = chartItms.length; i < l; i++) {
	var v = $(chartItms[i]).attr("data-v");
	myChart[i] = echarts.init(chartItms[i]);
	myChart[i].setOption(option[v]);
};
function y_show(val) {
	if(parseInt(val)>100000000){
		return parseInt(val / 100000000) +"亿";
	}else  if(parseInt(val)>10000){
		return parseInt(val / 10000) +"万";
	}else{
		return val;
	}
}
function mn_bf(bf_odds,bfv_list){
	var sum = 0,
		pl_all = 0,
		yk=[0,0,0],
		ykzs=[0,0,0],
		b_odds=$(".b_odds"),
		obj_yk = $(".b_yk"),
		obj_ykzj = $(".b_ykzs");
	for (var i = 0; i < bfv_list.length; i++) {
		sum 	+= parseFloat(bfv_list[i]);
		pl_all  += parseFloat(bf_odds[i]);
	}
	for (var j = 0; j < 3; j++) {
		yk[j] =  Math.round(sum - (bf_odds[j]*bfv_list[j]));
        sum = sum?sum:1;
		ykzs[j] = Math.round(((sum - (bf_odds[j]*bfv_list[j]))/sum)*100);
		//console.log(bf_odds[j]?bf_odds[j]:'-')
		b_odds.eq(j).html(bf_odds[j]?bf_odds[j]:'-');
		obj_yk.eq(j).html(yk[j]?yk[j]:'-');
		obj_ykzj.eq(j).html(ykzs[j]?ykzs[j]:'-');
	}
	
}
$(".bet-sxpl").change(function(){
	var cid = $(this).val(),
		bf_odds  = trade_odds.split(","),//必发率
		bfv_list = trade_list.split(",");//必发交易量
	if(bfv_list && bfv_list.length==3){
		if(cid!='bf'){
			$.ajax({
				url : "../fenxi1/inc/bet_ajax.php",
				type : "GET",
				dataType: "json",
				data : {"fid":fid,"cid" : cid},
				cache: false,
				success : function(d){
                    if(d &&  d.code && d.code == -100){
                        window.location.href = window.location.protocol +"//passport.500.com/user/login/?backurl=" + encodeURIComponent(window.location.href)
                    }
					if(d && d.length ==3){
						mn_bf(d,bfv_list);
					}else{
						$(".b_odds").html("-");
						$(".b_yk").html("-");
						$(".b_ykzs").html("-");
					}
					
				}
			});
		}else{
			mn_bf(bf_odds,bfv_list);
		}
	}else{
		console.log('bfv_list error!');
	}
});

$(".sale-tab li").click(function(){
	 var i = $(this).index();
	  $(this).addClass("cur");
	  $(this).siblings(".sale-tab li").removeClass("cur")
	  $(".sale-line").addClass("hide")
	  $(".sale-line").eq(i).removeClass("hide");
});