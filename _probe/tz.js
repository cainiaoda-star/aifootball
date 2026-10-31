// 评论
var id = $('#id').val();
(function(window, type, id){
	var $ = window.jQuery,
		CmtCenter = window.CmtCenter,
		box = $("#comment_box"),
		btn = $("#comment_btn"),
		text = $("#comment_text"),
		text_init_content=text.val(),
		timeOffset = 0,
		posting,
		space = window.subsites ? subsites.space : "http://space.500.com";
		tpl = [
	        '<li>',
	        '<span class="gray">{$time}</span>{$name}',
			'<p class="f14">{$content}</p>',
			'</li>'].join("");
	reportClick();
	// 获取评论列表
	function fetch() {
		CmtCenter.fetchList({
			data: {
				type: type,
				tid: id,
				order: "new",
				time: "yes",
				size: 0
			},
			success: function(d){
				var list = d.comments,
					html = [];
				for (var i = 0; i < list.length; ++ i) {
					html.push(build(list[i]));
				}
				box.html(html.join(""));
				reportClick();
				timeOffset = + (new Date(d.server_time)) - (new Date());
			},
			error: function(code, msg){
				var retry = $('<a href="javascript:;">重试</a>');
				box.html("加载失败，" + msg + "。").append(retry.click(fetch));
			}
		});
	}
	// 构建评论html
	function build(row) {
		var data = {
			name: row.username.substr(0,1)+"******",
			time: CmtCenter.formatTime(row.comment_time, timeOffset),
			content: row.content
		};
		return tpl.replace(/\{\$(\w+)\}/g, function(a, b){
			return (b in data) ? data[b] : "";
		});
	}
    function getCookie(name, value, expires) {//获取cookie值
			if (typeof value != 'undefined') {
				var expires = !value ? -1 : expires;
				if (typeof expires == 'number') {
					var date = new Date();
					date.setTime(date.getTime() + (expires * 60 * 1000));
					expires = '; expires=' + date.toUTCString();
				} else {
					expires = '';
				}
				var _domain = location.host.indexOf('.500.com') != -1 ? '500.com' : '500boss.com';
				document.cookie = [name, '=', encodeURIComponent(value), expires, '; path=/', '; domain='+_domain].join('');
			} else {
				if (m = new RegExp("(^|)" + name + "=([^;]*)(;|$)", "gi").exec(document.cookie)) {
					return decodeURIComponent(m[2]);
				}
				return null;
			}
    }
    
    //判断登陆
    function isLogin() {
        var user = getCookie('ck_user');
        if (!user || (typeof arguments[0] != 'undefined' && arguments[0])) {
            location.href = '//passport.500.com/user/login/?backurl='+encodeURIComponent(location.href) ;;
            return false;
        }
        return true;
    }
	//文本框事件
	text.focus(function(){
		
		$(this).css("color","#333").val('');
	});
	text.blur(function(){
		var t=$(this).val();
		if(t=='' || t==text_init_content){
			$(this).css("color","#999").val(text_init_content);
		}
	});
	// 提交
	btn.click(function(){
		var content = $.trim(text.val());
		if (content == "") {
			alert("请输入评论内容");
			return;
		}
		if (posting) {
			return;
		}
        // 登录判断
		posting = true;
		var flag = isLogin();
		if ( !flag ) {
			return false;
		}
		btn.attr("disabled", "disabled");
		CmtCenter.post({
			data: {
				type: type,
				tid: id,
				content: content
			},
			success: function(d){
				box.prepend(build(d));
				var con = box.parent().get(0);
				if (con) {
					con.scrollTop = 0;
				}
				reportClick();
				text.val("");
			},
			complete: function(){
				posting = false;
				btn.removeAttr("disabled");
			},
			error: function(code, msg){
                if(parseInt(code)==1){//服务器端登录失效
                    location.href = '//passport.500.com/user/login/?backurl='+encodeURIComponent(location.href) ;;
                }else{
                  alert(msg);  
                }
				
			}
		});
	});

	//举报
	function reportClick(){
		$(".jubao-bn").unbind();
		$(".jubao-bn").click(function(){
			var t=$(this), cid = t.attr("data-v");
			CmtCenter.report({
				data: {
					cid: cid
				},
				success: function(d){
					if(0==d){
						t.addClass("on").unbind().html("已举报");
					}
				},
				error: function(code, msg){
					alert(msg);
				}
			});
		});
	}
	// 获取评论列表
	if (box.length) {
		//fetch();
	}
})(window, 2, id);